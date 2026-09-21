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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        _logger.LogInformation("🚀 CallLogPollingService starting...");

        if (_lateAttachEnabled)
        {
            try
            {
                if (_amoService.IsExpired())
                {
                    await _amoService.InitializeAsync();
                }
                await EnsureAuthorized();
                await RunStartupCatchUpAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup catch-up pass failed; continuing with regular polling.");
            }
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

    // Однократный догоняющий проход при старте: основной опрос смотрит
    // только ~5 минут назад, поэтому звонки существующим контактам за время
    // простоя (рестарт/деплой) иначе теряются — поздняя привязка их не
    // подберёт, т.к. эти контакты не менялись. Идемпотентность через uniq
    // (NoteExistsAsync/guard) делает повторный проход безопасным.
    private async Task RunStartupCatchUpAsync()
    {
        var dateFrom = DateTime.UtcNow.AddHours(-_startupLookbackHours);
        _logger.LogInformation("Startup catch-up: fetching calls since {DateFrom}", dateFrom);

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
            var callLogs = await _rc.Restapi().Account().CallLog().List(callLogParameters);

            if (callLogs?.records == null || callLogs.records.Length == 0)
            {
                break;
            }

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

                    await _amoService.ProcessSingleCallAsync(record, _guard, "STARTUP", callStartUtc);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Startup catch-up: error processing call {RecordId}", record.id);
                }
            }

            if (callLogs.records.Length < CallLogPageSize)
            {
                break;
            }
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
                var callLogs = await _rc.Restapi().Account().CallLog().List(callLogParameters);

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
