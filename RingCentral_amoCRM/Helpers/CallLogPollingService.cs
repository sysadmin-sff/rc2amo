using Microsoft.Extensions.Logging;
using RingCentral;
using RingCentral_amoCRM.Helpers;
using RingCentral_amoCRM.Models;
using CallLogRecord = RingCentral.CallLogRecord;

public class CallLogPollingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CallLogPollingService> _logger;
    private readonly RestClient _rc;
    private readonly string _jwt;
    private DateTime _expiresAt;
    private readonly AmoCrmService _amoService;
    private readonly TimeSpan _pollInterval = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _recentCallWindow = TimeSpan.FromMinutes(5);
    private readonly DateTime _lastCallTime;

    // Дедупликация: RingCentral отдаёт один и тот же звонок в нескольких соседних
    // опросах, пока он не выйдет из окна _recentCallWindow. Без этого набора
    // в сделку падает несколько одинаковых заметок про один созвон. Это
    // быстрый pre-check в памяти перед обращением к amoCRM (NoteExistsAsync) —
    // источник истины при рестарте сервиса именно amoCRM, а не этот guard.
    // Общий с LateAttachService (см. CallProcessingGuard).
    private readonly CallProcessingGuard _guard;

    private readonly bool _lateAttachEnabled;
    private readonly int _startupLookbackHours;
    private readonly int _startupCatchUpDelaySeconds;

    public CallLogPollingService(
        IServiceProvider serviceProvider,
        ILogger<CallLogPollingService> logger,
        RestClient rc,
        IConfiguration configuration,
        AmoCrmService amoService,
        CallProcessingGuard guard)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _rc = rc;
        _amoService = amoService;
        _guard = guard;
        _jwt = configuration.GetSection("Credentials")["JWT"];
        
        // Логируем наличие JWT токена (без раскрытия содержимого)
        if (string.IsNullOrWhiteSpace(_jwt))
        {
            _logger.LogError("⚠️ JWT token is EMPTY or NULL in configuration! RingCentral authorization will fail.");
        }
        else
        {
            _logger.LogInformation("✅ JWT token loaded successfully (length: {Length} chars)", _jwt.Length);
        }
        
        var expiresAtStr = configuration.GetSection("Credentials")["ExpiresAt"];
        _expiresAt = string.IsNullOrEmpty(expiresAtStr) ? DateTime.UtcNow : DateTime.Parse(expiresAtStr);

        _lateAttachEnabled = configuration.GetValue("LateAttach:Enabled", true);
        _startupLookbackHours = configuration.GetValue("LateAttach:StartupLookbackHours", 24);

        // Разносим старт трёх фоновых сервисов (POLL/STARTUP/LATE), чтобы они не
        // били по одному и тому же heavy-group rate limit RC (call-log) все разом
        // при запуске процесса. POLL и STARTUP делят один хост-сервис
        // (CallLogPollingService), поэтому у STARTUP отдельная задержка сверх
        // базовой 10с — LateAttachService (другой сервис) разводится своей.
        _startupCatchUpDelaySeconds = configuration.GetValue("Startup:CatchUpDelaySeconds", 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        _logger.LogInformation("🚀 CallLogPollingService starting...");

        if (_lateAttachEnabled)
        {
            // Запускается параллельно с POLL-циклом ниже, не блокируя его: STARTUP —
            // тяжёлый (широкое окно, много страниц call-log) и не срочный (покрывает
            // уже случившийся простой) разовый проход, а POLL должен начать опрашивать
            // свежие звонки без задержки. Задержка внутри самого прохода разводит его
            // во времени с первым POLL-запросом на общем heavy-group rate limit RC —
            // если await'ить эту паузу здесь же, она задержала бы и сам POLL.
            _ = RunDelayedStartupCatchUpAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_amoService.IsExpired())
                {
                    await _amoService.InitializeAsync();
                }
                await EnsureAuthorized();
                await ProcessCallLogsAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error during call log polling.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("CallLogPollingService is stopping.");
    }

    private async Task EnsureAuthorized()
    {
        _logger.LogInformation($"Ensuring RingCentral client is authorized... token exp.{_expiresAt} date now {DateTime.UtcNow}");
        
        // Проверяем наличие JWT токена
        if (string.IsNullOrWhiteSpace(_jwt))
        {
            _logger.LogError("❌ JWT token is empty! Cannot authorize RingCentral. Check appsettings.json Credentials:JWT");
            throw new InvalidOperationException("JWT token is not configured. Please check appsettings.json.");
        }
        
        // Проверяем токен RingCentral с буфером в 5 минут перед истечением
        if (_rc.token == null || _expiresAt.AddMinutes(-5) <= DateTime.UtcNow)
        {
            _logger.LogInformation("RingCentral token is null or expiring soon, authorizing with JWT (length: {Length})...", _jwt.Length);
            
            try
            {
                var token = await _rc.Authorize(_jwt);
                
                // Обновляем только время истечения, НЕ ПЕРЕЗАПИСЫВАЕМ JWT!
                _expiresAt = DateTime.UtcNow.AddSeconds(token.expires_in ?? 3600);
                UpdateApp.UpdateAppSetting("Credentials:ExpiresAt", _expiresAt.ToString("o"));
                
                _logger.LogInformation("✅ RingCentral authorized successfully. Token expires at {ExpiresAt}", _expiresAt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to authorize RingCentral with JWT (JWT length: {Length})", _jwt?.Length ?? 0);
                throw;
            }
        }
        else
        {
            _logger.LogInformation("RingCentral token is still valid until {ExpiresAt}", _expiresAt);
        }
    }

    private bool IsRecentCall(CallLogRecord record)
    {
        if (string.IsNullOrEmpty(record.startTime))
            return false;

        _logger.LogInformation("Call {RecordId} started at {StartTime}, time now {now}",
            record.id, record.startTime, DateTime.UtcNow);
        // startTime приходит в ISO-8601 с суффиксом Z (UTC). DateTime.Parse по умолчанию
        // переводит это в локальное время сервера, и вычитание из UtcNow даёт ошибку
        // на величину смещения зоны (у нас −4/−5 часов). Разбираем строго как UTC.
        var startUtc = DateTime.Parse(record.startTime, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal);
        var EndTime = startUtc.AddSeconds((double)record.duration);
        var timeSinceModified = DateTime.UtcNow - EndTime;
        _logger.LogInformation($"Call ended {timeSinceModified.TotalMinutes:F1} minutes ago");
        return timeSinceModified.TotalMinutes <= _recentCallWindow.TotalMinutes;
    }

    private const int CallLogPageSize = 100;
    private const int CallLogMaxPages = 10;

    // Догоняющий проход при старте покрывает StartupLookbackHours (по
    // умолчанию 24ч) — заметно более широкое окно, чем ~15 минут обычного
    // поллинга, поэтому ему нужен собственный, больший предел страниц.
    private const int StartupCatchUpMaxPages = 50;

    // Обёртка над AmoCrmService.RunWithRcRetryAsync для одной страницы журнала
    // звонков: логирует явный CYCLE-abort с причиной (rate_limit после
    // исчерпания повторов, либо прочий сбой) и сигнализирует вызывающему коду
    // через failed=true остановить пагинацию, не бросая исключение наружу —
    // догоняющий проход при старте не должен валить весь ExecuteAsync.
    private async Task<(T result, bool failed)> TryFetchCallLogPageAsync<T>(
        string logPrefix, Func<Task<T>> fetch, int page)
    {
        try
        {
            var result = await _amoService.RunWithRcRetryAsync(fetch, $"{logPrefix} call-log page={page}");
            return (result, false);
        }
        catch (Exception ex)
        {
            var reason = AmoCrmService.IsRcRateLimitException(ex) ? "rate_limit" : "call_log_failed";
            _logger.LogError(ex, "{Prefix} CYCLE aborted reason={Reason} page={Page}, catch-up incomplete.", logPrefix, reason, page);
            return (default, true);
        }
    }

    // Запускается как fire-and-forget из ExecuteAsync (см. комментарий там),
    // не блокируя POLL-цикл. Собственный try/catch — это единственное место,
    // которое теперь ловит ошибки догоняющего прохода: раньше их ловил
    // ExecuteAsync, оборачивавший await RunStartupCatchUpAsync() напрямую.
    private async Task RunDelayedStartupCatchUpAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation(
                "Startup catch-up: delaying {DelaySeconds}s to avoid competing with POLL for RC rate limit",
                _startupCatchUpDelaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(_startupCatchUpDelaySeconds), stoppingToken);

            // ПРИМЕЧАНИЕ: IsExpired/InitializeAsync/EnsureAuthorized не защищены
            // локом и теперь могут выполняться параллельно с тем же вызовом внутри
            // POLL-цикла ниже (не блокируем его специально — см. комментарий в
            // ExecuteAsync). Это расширяет уже существующую (до этой правки)
            // гонку за обновление токена между CallLogPollingService и
            // LateAttachService на ещё один путь внутри одного сервиса; при
            // задержке по умолчанию (60с против 10с у POLL) окно гонки на практике
            // узкое — POLL почти всегда успевает авторизоваться первым. Общая
            // защита токена локом — отдельная задача, не в рамках текущего фикса.
            if (_amoService.IsExpired())
            {
                await _amoService.InitializeAsync();
            }
            await EnsureAuthorized();
            await RunStartupCatchUpAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Штатное завершение при остановке сервиса — не ошибка.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup catch-up pass failed; regular polling is unaffected.");
        }
    }

    // Однократный догоняющий проход при старте: основной опрос смотрит
    // только ~5 минут назад, поэтому звонки существующим контактам за время
    // простоя (рестарт/деплой) иначе теряются — поздняя привязка их не
    // подберёт, т.к. эти контакты не менялись. Идемпотентность через uniq
    // (NoteExistsAsync/guard) делает повторный проход безопасным.
    private async Task RunStartupCatchUpAsync(CancellationToken stoppingToken)
    {
        var dateFrom = DateTime.UtcNow.AddHours(-_startupLookbackHours);
        _logger.LogInformation("Startup catch-up: fetching calls since {DateFrom}", dateFrom);

        int totalFetched = 0;
        bool lastPageWasFull = false;
        int pagesFetched = 0;
        for (int page = 1; page <= StartupCatchUpMaxPages; page++)
        {
            var callLogParameters = new ReadCompanyCallLogParameters()
            {
                perPage = CallLogPageSize,
                page = page,
                view = "Detailed",
                withRecording = true,
                dateFrom = dateFrom.ToString("o"),
            };
            var (callLogs, callLogFailed) = await TryFetchCallLogPageAsync(
                "STARTUP", () => _rc.Restapi().Account().CallLog().List(callLogParameters), page);
            if (callLogFailed)
            {
                break;
            }

            if (callLogs?.records == null || callLogs.records.Length == 0)
            {
                lastPageWasFull = false;
                break;
            }

            pagesFetched = page;
            totalFetched += callLogs.records.Length;

            foreach (var record in callLogs.records)
            {
                try
                {
                    if (record.from?.extensionId != null && record.to?.extensionId != null)
                    {
                        continue;
                    }

                    DateTime? callStartUtc = string.IsNullOrEmpty(record.startTime)
                        ? null
                        : DateTime.Parse(record.startTime, null,
                            System.Globalization.DateTimeStyles.AdjustToUniversal |
                            System.Globalization.DateTimeStyles.AssumeUniversal);

                    // failClosed: true — это разовый проход при старте, следующего
                    // шанса догнать конкретно эти звонки нет в рамках самого прохода,
                    // но идемпотентность через uniq делает безопасным полагаться на
                    // то, что не помеченный обработанным звонок подхватит либо
                    // поздняя привязка (если для него позже появится контакт), либо
                    // следующий деплой/рестарт повторит этот же проход. По той же
                    // причине неудачное скачивание записи (CMN-301 после повторов)
                    // тоже откладывает звонок целиком, а не создаёт заметку без записи.
                    await _amoService.ProcessSingleCallAsync(record, _guard, "STARTUP", callStartUtc, failClosed: true);

                    // Пауза между звонками с записью — при догоне после простоя записей
                    // много, а /recording/{id}/content — heavy-group эндпоинт RC с
                    // жёстким rate limit. Без паузы догон сам провоцирует CMN-301 на
                    // каждом следующем звонке подряд.
                    if (record.recording?.id != null)
                    {
                        await Task.Delay(AmoCrmService.RecordingDownloadBatchPause, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Startup catch-up: error processing call {RecordId}", record.id);
                }
            }

            lastPageWasFull = callLogs.records.Length == CallLogPageSize;
            if (!lastPageWasFull)
            {
                break;
            }
        }

        if (lastPageWasFull && pagesFetched == StartupCatchUpMaxPages)
        {
            _logger.LogWarning(
                "Startup catch-up: hit page cap ({MaxPages} pages), some calls in the {Hours}h window may not have been scanned",
                StartupCatchUpMaxPages, _startupLookbackHours);
        }

        _logger.LogInformation("Startup catch-up complete: {Total} calls scanned", totalFetched);
    }

    private async Task ProcessCallLogsAsync()
    {
        try
        {
            // Набор обработанных ID чистим раз в сутки: окно свежести всего 5 минут,
            // так что старые записи держать смысла нет, а память расти не должна.
            _guard.ClearOlderThanIfDue(TimeSpan.FromHours(24));

            // Окно выборки: чуть шире окна свежести звонка, с запасом на случай
            // задержек в самом Call Log API.
            var dateFrom = DateTime.UtcNow.Subtract(_recentCallWindow + TimeSpan.FromMinutes(10));

            int totalFetched = 0;
            for (int page = 1; page <= CallLogMaxPages; page++)
            {
                var callLogParameters = new ReadCompanyCallLogParameters()
                {
                    perPage = CallLogPageSize,
                    page = page,
                    view = "Detailed",
                    withRecording = true,
                    dateFrom = dateFrom.ToString("o"),
                };
                var (callLogs, callLogFailed) = await TryFetchCallLogPageAsync(
                    "POLL", () => _rc.Restapi().Account().CallLog().List(callLogParameters), page);
                if (callLogFailed)
                {
                    break;
                }

                if (callLogs?.records == null || callLogs.records.Length == 0)
                {
                    break;
                }

                totalFetched += callLogs.records.Length;
                _logger.LogInformation("📞 Fetched {Count} call log records (page {Page})", callLogs.records.Length, page);

                foreach (var record in callLogs.records)
                {
                    try
                    {
                        if (!IsRecentCall(record))
                        {
                            _logger.LogInformation($"Skipping old call {record.id}");
                            continue;
                        }

                        // Внутренний звонок: у обеих сторон есть extensionId,
                        // значит это сотрудник-сотрудник, искать сделку не по чему.
                        if (record.from?.extensionId != null && record.to?.extensionId != null)
                        {
                            _logger.LogInformation($"Skipping internal call {record.id} (employee-to-employee)");
                            continue;
                        }

                        if (_guard.IsProcessed(record.id))
                        {
                            _logger.LogInformation($"Call {record.id} already processed, skipping duplicate");
                            continue;
                        }

                        _logger.LogInformation($"Start processing call {record.id} call completed at {record.startTime}");
                        await _amoService.ProcessSingleCallAsync(record, _guard, "POLL");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing call record ID: {RecordId}, will retry on next poll", record.id);
                    }
                }

                if (callLogs.records.Length < CallLogPageSize)
                {
                    break;
                }
            }

            if (totalFetched == 0)
            {
                _logger.LogInformation("No call log records found.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching call logs from RingCentral API.");
        }
    }

}
