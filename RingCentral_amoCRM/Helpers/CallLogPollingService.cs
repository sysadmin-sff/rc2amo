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

    // Значения RingCentral CallLogRecord.result, означающие, что разговор не
    // состоялся (звонок пропущен/не принят/ушёл на автоответчик и т.п.).
    private static readonly HashSet<string> MissedCallResults = new(StringComparer.OrdinalIgnoreCase)
    {
        "Missed", "No Answer", "Voicemail", "Rejected", "Busy", "Abandoned"
    };

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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        _logger.LogInformation("🚀 CallLogPollingService starting...");

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
                        await ProcessCallRecordAsync(record);

                        // Помечаем как обработанный ТОЛЬКО после успешного завершения
                        // (в том числе закономерных "не нашли лид"/"skip"), а не до
                        // вызова — иначе сбой amoCRM внутри ProcessCallRecordAsync
                        // навсегда потеряет звонок в пределах окна свежести: запись
                        // уйдёт в guard ещё ДО того, как заметка реально
                        // создана, и повторный опрос её больше не тронет.
                        _guard.MarkProcessed(record.id);
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

    private async Task ProcessCallRecordAsync(CallLogRecord record)
    {
        if (record.from == null || record.to == null)
        {
            _logger.LogWarning("Skipping call {RecordId}: missing from or to information", record.id);
            return;
        }

        _logger.LogInformation($"Processing call: ID={record.id}, From={record.from}, To={record.to}, Direction={record.direction}, Result={record.result}, Duration={record.duration}s");

        // Vneshniy abonent ne imeet extensionId: dlya vhodyashchih ishchem po from,
        // dlya ishodyashchih (zvonit nash sotrudnik) - po to.
        var searchNumber = record.from.extensionId == null
            ? record.from.phoneNumber
            : record.to.phoneNumber;

        if (string.IsNullOrWhiteSpace(searchNumber))
        {
            _logger.LogWarning("Skipping call {RecordId}: no usable phone number to search", record.id);
            return;
        }

        var candidateLeads = await _amoService.FindLeadByPhoneNumberAsync(searchNumber);
        if (candidateLeads == null)
        {
            _logger.LogInformation("No leads found for phone number {Number}", searchNumber);
            return;
        }

        var targetLeadId = await _amoService.ResolveTargetLeadAsync(candidateLeads);
        if (targetLeadId == null)
        {
            _logger.LogWarning("Could not resolve a target lead for call {RecordId} (number {Number})", record.id, searchNumber);
            return;
        }

        var noteType = record.direction == "Inbound" ? "call_in" : "call_out";

        if (await _amoService.NoteExistsAsync(targetLeadId.Value, noteType, record.id))
        {
            _logger.LogInformation("Call {RecordId} already has a note on lead {LeadId}, skipping", record.id, targetLeadId);
            return;
        }

        // Загружаем запись в хранилище amoCRM для получения постоянной ссылки
        string permanentRecordingUrl = null;

        if (record.recording?.id != null)
        {
            permanentRecordingUrl = await _amoService.UploadCallRecordingAsync(record.recording.id, record.id);

            if (string.IsNullOrEmpty(permanentRecordingUrl))
            {
                _logger.LogWarning("Failed to upload recording for call {CallId}, note will be created without recording link", record.id);
            }
        }
        else
        {
            _logger.LogInformation("Call {CallId} has no recording", record.id);
        }

        bool isMissed = record.result != null && MissedCallResults.Contains(record.result);

        await _amoService.CreateCallNoteAsync(targetLeadId.Value, record, permanentRecordingUrl, searchNumber, isMissed);
        _logger.LogInformation("✅ Note added to lead {LeadId} for call {RecordId} from {From}", targetLeadId, record.id, record.from.phoneNumber);
    }
}
