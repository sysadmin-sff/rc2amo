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
    // в сделку падает несколько одинаковых заметок про один созвон.
    private readonly HashSet<string> _processedCallIds = new();
    private DateTime _lastProcessedCleanup = DateTime.UtcNow;

    public CallLogPollingService(
        IServiceProvider serviceProvider,
        ILogger<CallLogPollingService> logger,
        RestClient rc,
        IConfiguration configuration,
        AmoCrmService amoService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _rc = rc;
        _amoService = amoService;
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

    private async Task ProcessCallLogsAsync()
    {
        try
        {
            var callLogParameters = new ReadCompanyCallLogParameters()
            {
                perPage = 20,
                view = "Detailed",
                withRecording = true,
            };
            var callLogs = await _rc.Restapi().Account().CallLog().List(callLogParameters);

            // Набор обработанных ID чистим раз в сутки: окно свежести всего 5 минут,
            // так что старые записи держать смысла нет, а память расти не должна.
            if ((DateTime.UtcNow - _lastProcessedCleanup).TotalHours >= 24)
            {
                _logger.LogInformation($"Clearing processed call IDs cache ({_processedCallIds.Count} entries)");
                _processedCallIds.Clear();
                _lastProcessedCleanup = DateTime.UtcNow;
            }

            if (callLogs?.records != null && callLogs.records.Length > 0)
            {
                _logger.LogInformation("📞 Fetched {Count} call log records", callLogs.records.Length);

                foreach (var record in callLogs.records)
                {
                    try
                    {
                        if (IsRecentCall(record))
                        {
                            if (_processedCallIds.Contains(record.id))
                            {
                                _logger.LogInformation($"Call {record.id} already processed, skipping duplicate");
                                continue;
                            }
                            _processedCallIds.Add(record.id);

                            _logger.LogInformation($"Start processing call {record.id} call completed at {record.startTime}");
                            await ProcessCallRecordAsync(record);
                        }
                        else
                        {
                            _logger.LogInformation($"Skipping old call {record.id}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing call record ID: {RecordId}", record.id);
                    }
                }
            }
            else
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

        var leads = await _amoService.FindLeadByPhoneNumberAsync(searchNumber);

        if (leads != null)
        {
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

            foreach (var leadId in leads)
            {
                await _amoService.CreateCallNoteAsync(leadId, record, permanentRecordingUrl, searchNumber);
                _logger.LogInformation("✅ Note added to lead {LeadId} for call from {From}", leadId, record.from.phoneNumber);
            }
        }
        else
        {
            _logger.LogInformation("No leads found for phone number {Number}", searchNumber);
        }
    }
}
