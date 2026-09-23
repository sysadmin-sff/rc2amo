using Microsoft.Extensions.Logging;
using RingCentral;
using RingCentral_amoCRM.Models;
using CallLogRecord = RingCentral.CallLogRecord;

namespace RingCentral_amoCRM.Helpers;

public class LateAttachService : BackgroundService
{
    private readonly ILogger<LateAttachService> _logger;
    private readonly AmoCrmService _amoService;
    private readonly CallProcessingGuard _guard;
    private readonly RestClient _rc;

    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly int _lookbackDays;
    private readonly int _startupLookbackHours;

    private DateTime? _lastCycleStartUtc;

    private const int CallLogPageSize = 100;
    private const int CallLogMaxPages = 50;

    public LateAttachService(
        ILogger<LateAttachService> logger,
        AmoCrmService amoService,
        CallProcessingGuard guard,
        RestClient rc,
        IConfiguration configuration)
    {
        _logger = logger;
        _amoService = amoService;
        _guard = guard;
        _rc = rc;

        _enabled = configuration.GetValue("LateAttach:Enabled", true);
        _interval = TimeSpan.FromMinutes(configuration.GetValue("LateAttach:IntervalMinutes", 15));
        _lookbackDays = configuration.GetValue("LateAttach:LookbackDays", 14);
        _startupLookbackHours = configuration.GetValue("LateAttach:StartupLookbackHours", 24);

        _logger.LogInformation(
            "LATE settings: enabled={Enabled} intervalMin={IntervalMin} lookbackDays={LookbackDays} startupLookbackH={StartupLookbackH}",
            _enabled, _interval.TotalMinutes, _lookbackDays, _startupLookbackHours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("LateAttachService disabled via LateAttach:Enabled=false, not starting.");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LATE cycle failed with an unhandled error; cycle start point not advanced.");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        var cycleStartUtc = DateTime.UtcNow;
        var sinceUtc = _lastCycleStartUtc ?? cycleStartUtc.AddHours(-_startupLookbackHours);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int contactsCount = 0, leadsCount = 0, phonesCount = 0, matchedCount = 0;
        int attached = 0, dup = 0, errors = 0;

        List<AmoCrmContact> updatedContacts;
        List<AmoCrmLeadDetail> updatedLeads;

        try
        {
            if (_amoService.IsExpired())
            {
                await _amoService.InitializeAsync();
            }

            updatedContacts = await _amoService.GetUpdatedContactsAsync(sinceUtc);
            updatedLeads = await _amoService.GetUpdatedLeadsWithContactsAsync(sinceUtc);
        }
        catch (Exception ex)
        {
            // Точка отсчёта НЕ продвигается: следующий цикл повторит тот же
            // sinceUtc, чтобы не потерять изменения из-за временного сбоя amoCRM.
            _logger.LogError(ex, "LATE cycle: amoCRM request failed, cycle start point not advanced.");
            return;
        }

        contactsCount = updatedContacts.Count;
        leadsCount = updatedLeads.Count;

        var phones = AmoCrmService.ExtractNormalizedPhones(updatedContacts);

        // Сделки без контактов бесполезны — с контактами, у которых нет
        // телефонов в custom_fields_values, тоже. ExtractNormalizedPhones
        // сам пропускает контакты без поля PHONE.
        var leadContacts = updatedLeads
            .Where(l => l.Embedded?.Contacts != null)
            .SelectMany(l => l.Embedded.Contacts)
            .Select(c => new AmoCrmContact { Id = c.Id })
            .ToList();

        // Контакты, пришедшие только через сделки, не несут custom_fields_values
        // (в ответе /leads?with=contacts amoCRM отдаёт только id/name) — их
        // телефоны нужно дотянуть отдельным запросом контактов по id, иначе
        // ExtractNormalizedPhones для них всегда вернёт пусто.
        if (leadContacts.Count > 0)
        {
            var leadContactIds = leadContacts.Select(c => c.Id).Distinct().ToList();
            List<AmoCrmContact> fetchedLeadContacts;
            try
            {
                fetchedLeadContacts = await _amoService.GetContactsByIdsAsync(leadContactIds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LATE cycle: fetching contacts for updated leads failed, cycle start point not advanced.");
                return;
            }

            foreach (var p in AmoCrmService.ExtractNormalizedPhones(fetchedLeadContacts))
            {
                phones.Add(p);
            }
        }

        phonesCount = phones.Count;

        if (phones.Count == 0)
        {
            sw.Stop();
            _logger.LogInformation(
                "LATE cycle contacts={Contacts} leads={Leads} phones=0 calls_matched=0 attached=0 dup=0 errors=0 duration={Duration:F1}",
                contactsCount, leadsCount, sw.Elapsed.TotalSeconds);
            _lastCycleStartUtc = cycleStartUtc;
            return;
        }

        List<CallLogRecord> candidateCalls;
        try
        {
            candidateCalls = await FetchCallLogAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LATE cycle: RingCentral call log request failed, cycle start point not advanced.");
            return;
        }

        var matched = candidateCalls.Where(r => MatchesAnyPhone(r, phones)).ToList();
        matchedCount = matched.Count;

        foreach (var record in matched)
        {
            var ageHours = string.IsNullOrEmpty(record.startTime)
                ? (double?)null
                : (DateTime.UtcNow - ParseStartUtc(record.startTime)).TotalHours;

            DateTime? callStartUtc = string.IsNullOrEmpty(record.startTime)
                ? null
                : ParseStartUtc(record.startTime);

            var searchNumber = record.from?.extensionId == null
                ? record.from?.phoneNumber
                : record.to?.phoneNumber;

            _logger.LogInformation("LATE id={Id} number={Number} age_h={AgeH:F1}", record.id, searchNumber, ageHours);

            CallProcessingResult result;
            try
            {
                result = await _amoService.ProcessSingleCallAsync(record, _guard, "LATE", callStartUtc, failClosed: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LATE id={Id} action=error", record.id);
                errors++;
                continue;
            }

            switch (result)
            {
                case CallProcessingResult.Attached:
                    attached++;
                    break;
                case CallProcessingResult.Duplicate:
                    dup++;
                    break;
                case CallProcessingResult.Error:
                    errors++;
                    break;
            }

            // Пауза между звонками с записью — та же причина, что и в startup
            // catch-up: /recording/{id}/content — heavy-group эндпоинт RC, и
            // цикл LateAttach может встретить несколько таких звонков подряд.
            if (record.recording?.id != null)
            {
                await Task.Delay(AmoCrmService.RecordingDownloadBatchPause, stoppingToken);
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "LATE cycle contacts={Contacts} leads={Leads} phones={Phones} calls_matched={Matched} attached={Attached} dup={Dup} errors={Errors} duration={Duration:F1}",
            contactsCount, leadsCount, phonesCount, matchedCount, attached, dup, errors, sw.Elapsed.TotalSeconds);

        if (errors > 0)
        {
            // Хотя бы один звонок в цикле не удалось достоверно обработать
            // (fail-closed NoteExistsAsync не смог проверить наличие заметки,
            // либо ProcessSingleCallAsync упал по другой причине) — точку
            // отсчёта не продвигаем. Следующий цикл пересмотрит те же
            // изменения amoCRM; идемпотентность через uniq не даст
            // задублировать уже созданные заметки.
            _logger.LogWarning("LATE cycle had {Errors} error(s), cycle start point not advanced", errors);
            return;
        }

        // Точка отсчёта продвигается только когда весь цикл дошёл до конца
        // без ошибок обработки отдельных звонков.
        _lastCycleStartUtc = cycleStartUtc;
    }

    private async Task<List<CallLogRecord>> FetchCallLogAsync()
    {
        var result = new List<CallLogRecord>();
        var dateFrom = DateTime.UtcNow.AddDays(-_lookbackDays);

        for (int page = 1; page <= CallLogMaxPages; page++)
        {
            var parameters = new ReadCompanyCallLogParameters
            {
                perPage = CallLogPageSize,
                page = page,
                view = "Detailed",
                withRecording = true,
                dateFrom = dateFrom.ToString("o"),
            };

            var callLogs = await _rc.Restapi().Account().CallLog().List(parameters);
            if (callLogs?.records == null || callLogs.records.Length == 0)
            {
                break;
            }

            result.AddRange(callLogs.records);

            if (callLogs.records.Length < CallLogPageSize)
            {
                break;
            }
        }

        return result;
    }

    private static bool MatchesAnyPhone(CallLogRecord record, HashSet<string> phones)
    {
        if (record.from == null || record.to == null)
        {
            return false;
        }

        // Внутренние звонки (employee-to-employee) исключаем так же, как
        // основной поллинг.
        if (record.from.extensionId != null && record.to.extensionId != null)
        {
            return false;
        }

        var searchNumber = record.from.extensionId == null
            ? record.from.phoneNumber
            : record.to.phoneNumber;

        var normalized = AmoCrmService.NormalizePhone(searchNumber);
        return normalized != null && phones.Contains(normalized);
    }

    private static DateTime ParseStartUtc(string startTime)
    {
        return DateTime.Parse(startTime, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal);
    }
}
