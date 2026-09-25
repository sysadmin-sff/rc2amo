using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using RingCentral;
using RingCentral_amoCRM.Helpers;
using RingCentral_amoCRM.Models;
using System.Runtime.Intrinsics.Arm;
using System.Text.Json;

namespace RingCentral_amoCRM.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RingCentralWebHookController : ControllerBase
{
    private const string BaseTestResponse = "{\n     \"uuid\" : \"6099091745625140455\",\n     \"event\" : \"/restapi/v1.0/account/335731037/extension/3100110036/message-store/instant?type=SMS\",\n     \"timestamp\" : \"2025-11-14T20:48:17.505Z\",\n     \"subscriptionId\" : \"ee938ed7-cfde-4d67-89f9-6f58c10bfd47\",\n     \"ownerId\" : \"4064496036\",\n     \"body\" : {\n         \"id\" : \"3158995804037\",\n         \"to\" : [ {\n             \"phoneNumber\" : \"+12397442122\",\n             \"name\" : \"Muhammad SFF\",\n             \"location\" : \"Fort Myers, FL\",\n             \"target\" : true\n         } ],\n         \"from\" : {\n             \"phoneNumber\" : \"+79788172077\",\n             \"location\" : \"Jacksonville, FL\",\n             \"phoneNumberInfo\" : {\n                 \"countryCode\" : \"1\",\n                 \"nationalDestinationCode\" : \"904\",\n                 \"subscriberNumber\" : \"5157201\"\n             }\n         },\n         \"type\" : \"SMS\",\n         \"creationTime\" : \"2025-11-14T20:48:17.497Z\",\n         \"lastModifiedTime\" : \"2025-11-14T20:48:17.497Z\",\n         \"readStatus\" : \"Unread\",\n         \"priority\" : \"Normal\",\n         \"attachments\" : [ {\n             \"id\" : \"3158995804037\",\n             \"type\" : \"Text\",\n             \"contentType\" : \"text/plain\"\n         }, {\n             \"id\" : \"80488629037\",\n             \"type\" : \"MmsAttachment\",\n             \"uri\" : \"https://media.ringcentral.com/restapi/v1.0/account/335731037/extension/3100110036/message-store/3158995804037/content/80488629037\",\n             \"contentType\" : \"image/png\",\n             \"size\" : 481845\n         } ],\n         \"direction\" : \"Inbound\",\n         \"availability\" : \"Alive\",\n         \"subject\" : \" \",\n         \"messageStatus\" : \"Received\",\n         \"conversation\" : {\n             \"id\" : \"4028175899994129997\"\n         },\n         \"eventType\" : \"Create\",\n         \"owner\" : {\n             \"extensionId\" : \"3100110036\",\n             \"extensionType\" : \"User\",\n             \"name\" : \"Muhammad SFF\"\n         }\n     }\n }";
    private readonly RestClient _rc;
    private readonly ILogger<RingCentralWebHookController> _logger;
    private readonly string _jwt;
    private readonly AmoCrmService _amoService;
    private readonly bool _smsOutboundEnabled;
    private readonly bool _voicemailEnabled;
    private DateTime _expiresAt;

    public RingCentralWebHookController(
        ILogger<RingCentralWebHookController> logger,
        RestClient restClient,
        IConfiguration configuration,
        AmoCrmService amoService
        )
    {
        _logger = logger;
        _rc = restClient;
        _jwt = configuration.GetSection("Credentials")["JWT"];
        var expiresAtStr = configuration.GetSection("Credentials")["ExpiresAt"];
        _expiresAt = string.IsNullOrEmpty(expiresAtStr) ? DateTime.UtcNow : DateTime.Parse(expiresAtStr);
        _amoService = amoService;

        // По умолчанию выключено: новый (не-instant) фильтр подписки шлёт события
        // и на новые исходящие SMS, и на read/delete/status-change существующих —
        // при выключенном флаге всё это только логируется (см. HandleMessageStoreChangeAsync),
        // ничего не создаётся в amoCRM. Включается Sms__OutboundEnabled=true в
        // окружении после дня наблюдения за логами.
        _smsOutboundEnabled = configuration.GetValue("Sms:OutboundEnabled", false);

        // По умолчанию включено (в отличие от Sms:OutboundEnabled) — задача
        // явно просит Voicemail__Enabled=true по умолчанию. Выключатель
        // существует для безопасного отката: Voicemail__Enabled=false в
        // окружении полностью отключает фетч+создание заметок, оставляя
        // только логирование счётчиков (см. HandleNewVoicemailsAsync) — та же
        // модель безопасного отката, что и у Sms:OutboundEnabled, просто с
        // другим значением по умолчанию.
        _voicemailEnabled = configuration.GetValue("Voicemail:Enabled", true);
    }

    private async Task EnsureAuthorized()
    {
        _logger.LogInformation($"Ensuring RingCentral client is authorized... token exp.{_expiresAt} date now {DateTime.UtcNow}");
        
        // Проверяем токен RingCentral с буфером в 5 минут перед истечением
        if (_rc.token == null || _expiresAt.AddMinutes(-5) <= DateTime.UtcNow)
        {
            _logger.LogInformation("RingCentral token is null or expiring soon, authorizing with JWT...");
            
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
                _logger.LogError(ex, "❌ Failed to authorize RingCentral with JWT");
                throw;
            }
        }
        else
        {
            _logger.LogInformation("RingCentral token is still valid until {ExpiresAt}", _expiresAt);
        }
    }

    [HttpPost]
    [HttpGet]
    [Route("webhook")]
    public async  Task<IActionResult> HandleWebHookOrValidation()
    {
        if (Request.Headers.TryGetValue("Validation-Token", out var validationToken))
        {
            Response.Headers.Add("Validation-Token", validationToken);
            _logger.LogInformation("Received and returning validation token. Validation successful.");
            return Ok();
        }
        Request.Body.Seek(0, SeekOrigin.Begin);

        // Проверяем, есть ли что-то в потоке
        if (Request.ContentLength > 0)
        {
            var notificationJson = await new StreamReader(Request.Body).ReadToEndAsync();

            // Не-instant фильтр (message-store?type=SMS&direction=Outbound) шлёт
            // другую форму payload — сводку изменений (changes[].newCount/
            // updatedCount), без id/from/to. Различаем по наличию "changes" в JSON
            // до типизированного разбора, т.к. RingCentralNotification.Body для
            // такого payload не заполнится (там нет полей instant-события).
            if (notificationJson.Contains("\"changes\""))
            {
                // HandleMessageStoreChangeAsync разбирает совершенно новый, никогда
                // не проверенный на реальных данных payload (в отличие от instant-пути
                // ниже) — если разбор или обработка упадёт с исключением, RingCentral
                // должен всё равно получить 200 (иначе он начнёт повторную доставку,
                // а мы точно так же упадём на повторе). try/catch здесь — последний
                // рубеж на случай, если что-то внутри самого метода не поймает
                // исключение (см. также catch внутри метода).
                try
                {
                    return await HandleMessageStoreChangeAsync(notificationJson);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "HandleMessageStoreChangeAsync threw unhandled exception. WEBHOOK RAW {RawPayload}", notificationJson);
                    return Ok();
                }
            }

            // Инстант-путь (входящие SMS/MMS) — старый, месяцами работавший в
            // проде код, но БЕЗ какой-либо защиты от исключения: раньше здесь
            // не было try/catch вообще (в отличие от не-instant пути ниже,
            // который его получил в 0ea4cb8). Падение здесь (например,
            // Cannot get the value of a token type 'Number' as a string на
            // attachments[].size у MMS-вложения — см. Model.cs) уходило прямо
            // в Kestrel: RC получал 500, считал доставку неуспешной и повторял
            // её — то же самое падение на том же теле повторялось бесконечно,
            // а WEBHOOK RAW при этом никогда не логировался, потому что
            // логирование было только внутри HandleMessageStoreChangeAsync.
            // Оборачиваем весь инстант-путь (разбор + обработка), а не только
            // Deserialize — исключение из ProcessSmsMessageAsync (например,
            // сбой похода в amoCRM) должно точно так же не ронять запрос в 500.
            try
            {
                var notification = System.Text.Json.JsonSerializer.Deserialize<RingCentralNotification>(notificationJson);

                if (notification?.Body == null)
                {
                    _logger.LogWarning("WebHook received (no validation token), but body was empty or invalid JSON. Returning 200 OK.");
                    return Ok();
                }

                if ((notification.Body.Direction == "Inbound" || notification.Body.Direction == "Outbound") &&
                    notification.Body.Type == "SMS")
                {
                    // Instant-фильтр отдаёт только входящие (см. SubscriptionService) —
                    // это условие исторически покрывало оба направления "на будущее",
                    // но в реальности сюда приходит только Inbound. Оставляем как есть:
                    // поведение входящих не меняем.
                    await ProcessSmsMessageAsync(
                        notification.Body.Id,
                        notification.Body.Direction == "Outbound",
                        notification.Body.To?.FirstOrDefault()?.PhoneNumber,
                        notification.Body.To?.FirstOrDefault()?.Name,
                        notification.Body.From?.PhoneNumber,
                        notification.Body.From?.Name,
                        notification.Body.Subject);
                    return Created();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Instant SMS webhook path threw unhandled exception. WEBHOOK RAW {RawPayload}", notificationJson);
                return Ok();
            }
        }

        return Ok();
    }

    // Обрабатывает уведомление от не-instant message-store фильтра (сводка
    // изменений, добавлен для исходящих SMS — см. SubscriptionService). Логирует
    // всегда (в т.ч. в лог-онли режиме и для не-SMS/не-новых изменений — почасовая
    // сводка считает всё). Реальная выборка и создание заметок — только если
    // Sms:OutboundEnabled=true И это новое (не read/delete/status-change) SMS.
    private async Task<IActionResult> HandleMessageStoreChangeAsync(string notificationJson)
    {
        MessageStoreChangeNotification notification;
        try
        {
            notification = System.Text.Json.JsonSerializer.Deserialize<MessageStoreChangeNotification>(notificationJson);
        }
        catch (Exception ex)
        {
            // Модель этого payload построена по документации RC, а не по реальному
            // образцу (см. историю: упало на "Cannot get the value of a token type
            // 'Number' as a string" в проде) — логируем сырой JSON целиком, чтобы
            // можно было увидеть настоящую структуру и точно исправить модель,
            // а не гадать по одному полю за раз.
            _logger.LogError(ex, "Failed to deserialize message-store change notification. WEBHOOK RAW {RawPayload}", notificationJson);
            return Ok();
        }

        var changes = notification?.Body?.Changes;

        if (changes == null || changes.Count == 0)
        {
            _logger.LogWarning("WEBHOOK message-store notification with no changes[], body: {Body}", notificationJson);
            return Ok();
        }

        var smsChange = changes.FirstOrDefault(c => c.Type == "SMS");
        // Регистронезависимо: eventFilter подписки требует "Voicemail" (см.
        // SubscriptionService), но неизвестно, эхом каким регистром сам RC
        // проставит changes[].type в теле вебхука — проверено на проде только
        // то, что eventFilter с "VoiceMail" (заглавная M) отклоняется целиком
        // (400 CMN-101), само значение type в уже принятом вебхуке отдельно
        // не проверялось. StringComparer.OrdinalIgnoreCase дешевле, чем
        // рисковать пропустить голосовые из-за расхождения в одну букву.
        var voicemailChange = changes.FirstOrDefault(c => string.Equals(c.Type, "VoiceMail", StringComparison.OrdinalIgnoreCase));
        var otherChanges = changes.Where(c => c.Type != "SMS" && !string.Equals(c.Type, "VoiceMail", StringComparison.OrdinalIgnoreCase)).ToList();

        var newCount = smsChange?.NewCount ?? 0;
        var updatedCount = smsChange?.UpdatedCount ?? 0;
        var otherCount = otherChanges.Sum(c => (c.NewCount ?? 0) + (c.UpdatedCount ?? 0));

        var vmNewCount = voicemailChange?.NewCount ?? 0;
        var vmUpdatedCount = voicemailChange?.UpdatedCount ?? 0;

        _logger.LogInformation(
            "WEBHOOK event=message-store extId={ExtensionId} type=SMS new={NewCount} updated={UpdatedCount} vmNew={VmNewCount} vmUpdated={VmUpdatedCount} otherTypes=[{OtherTypes}]",
            notification.Body.ExtensionId, newCount, updatedCount, vmNewCount, vmUpdatedCount, string.Join(",", otherChanges.Select(c => c.Type)));

        _amoService.RecordSmsWebhookSummary(newCount, updatedCount, otherCount);
        _amoService.RecordVoicemailWebhookSummary(vmNewCount, vmUpdatedCount);

        // Голосовая почта обрабатывается независимо от ветки SMS ниже: у
        // расширения может в одном уведомлении не быть новых SMS, но быть
        // новое голосовое (или наоборот) — оба флага проверяются, ни один
        // return здесь не должен пропускать другую ветку.
        if (voicemailChange != null && vmNewCount > 0)
        {
            await HandleNewVoicemailsAsync(notification.Body.ExtensionId, notification.Body.LastUpdated, vmNewCount);
        }
        // vmUpdatedCount>0 без vmNewCount (прослушано/удалено существующее) —
        // осознанно не обрабатывается, только вошло в лог выше (см. п.2 задачи:
        // фильтр без direction шлёт и статусные изменения, реагируем только на
        // действительно новые голосовые).

        if (smsChange == null || newCount <= 0)
        {
            // Ничего нового: только read/delete/status-change на уже существующих
            // SMS (updatedCount) и/или изменения прочих типов (fax/pager) —
            // не обрабатываем ни при каких условиях.
            return Ok();
        }

        if (!_smsOutboundEnabled)
        {
            _logger.LogInformation(
                "Sms:OutboundEnabled=false, not fetching {NewCount} new outbound SMS for extension {ExtensionId} (log-only)",
                newCount, notification.Body.ExtensionId);
            return Ok();
        }

        if (string.IsNullOrEmpty(notification.Body.ExtensionId))
        {
            _logger.LogWarning("WEBHOOK message-store notification missing extensionId, cannot fetch messages");
            return Ok();
        }

        // Курсор продвигается ДО фетча: если он ниже упадёт (ошибка запроса),
        // мы не должны повторно и бесконечно переспрашивать то же окно — та же
        // логика "лучше пропустить, чем задублировать", что и для самого курсора
        // (см. AdvanceOutboundSmsCheckpoint).
        var sinceUtc = _amoService.AdvanceOutboundSmsCheckpoint(notification.Body.ExtensionId, notification.Body.LastUpdated);

        GetMessageList fetched;
        try
        {
            fetched = await _amoService.FetchOutboundSmsAsync(notification.Body.ExtensionId, sinceUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch outbound SMS for extension {ExtensionId} since {SinceUtc}", notification.Body.ExtensionId, sinceUtc);
            return Ok();
        }

        var records = fetched?.records;
        if (records == null || records.Length == 0)
        {
            _logger.LogInformation("MessageStore.List for extension {ExtensionId} since {SinceUtc} returned no records", notification.Body.ExtensionId, sinceUtc);
            return Ok();
        }

        foreach (var msg in records)
        {
            if (msg.direction != "Outbound" || msg.type != "SMS")
            {
                continue; // на случай, если API вернул что-то за пределами фильтра
            }

            await ProcessSmsMessageAsync(
                msg.id?.ToString(),
                isOutbound: true,
                toNumber: msg.to?.FirstOrDefault()?.phoneNumber,
                toName: msg.to?.FirstOrDefault()?.name,
                fromNumber: msg.from?.phoneNumber,
                fromName: msg.from?.name,
                smsText: msg.subject);
        }

        return Ok();
    }

    // Реагирует на "есть новые голосовые" из не-instant сводки message-store
    // (см. HandleMessageStoreChangeAsync) — сам вебхук не несёт данные
    // сообщения (номер/запись/расшифровку), только счётчик, поэтому дальше
    // дозапрашиваем через MessageStore().List(messageType=VoiceMail), тот же
    // паттерн, что и у исходящих SMS (fetch-on-notify, а не постоянный
    // поллер). Ошибки логируются и проглатываются — тот же принцип "всегда
    // 200 для RC", что и у остального HandleMessageStoreChangeAsync.
    private async Task HandleNewVoicemailsAsync(string extensionId, DateTime? notificationLastUpdatedUtc, int newCount)
    {
        if (!_voicemailEnabled)
        {
            _logger.LogInformation(
                "Voicemail:Enabled=false, not fetching {NewCount} new voicemail(s) for extension {ExtensionId} (log-only)",
                newCount, extensionId);
            return;
        }

        if (string.IsNullOrEmpty(extensionId))
        {
            _logger.LogWarning("WEBHOOK message-store notification missing extensionId, cannot fetch voicemails");
            return;
        }

        // Курсор продвигается до фетча — тот же аргумент "пропустить лучше,
        // чем задублировать", что и у AdvanceOutboundSmsCheckpoint.
        var sinceUtc = _amoService.AdvanceVoicemailCheckpoint(extensionId, notificationLastUpdatedUtc);

        GetMessageList fetched;
        try
        {
            fetched = await _amoService.FetchVoicemailsAsync(extensionId, sinceUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch voicemails for extension {ExtensionId} since {SinceUtc}", extensionId, sinceUtc);
            return;
        }

        var records = fetched?.records;
        if (records == null || records.Length == 0)
        {
            _logger.LogInformation("MessageStore.List(VoiceMail) for extension {ExtensionId} since {SinceUtc} returned no records", extensionId, sinceUtc);
            return;
        }

        foreach (var msg in records)
        {
            // Регистронезависимо — та же осторожность, что и при разборе
            // changes[].type выше (см. HandleMessageStoreChangeAsync):
            // подтверждено на проде только то, что чтение через
            // ListMessagesParameters.messageType требует "VoiceMail"
            // (заглавная M), само поле type в ответе отдельно не сверялось.
            if (!string.Equals(msg.type, "VoiceMail", StringComparison.OrdinalIgnoreCase))
            {
                continue; // на случай, если API вернул что-то за пределами фильтра
            }

            await ProcessVoicemailAsync(extensionId, msg);
        }
    }

    // Обработка одного голосового сообщения: дедупликация, поиск сделки по
    // номеру звонившего, загрузка записи, создание заметки. Формат
    // наблюдаемости фиксирован задачей: "VM id=<id> number=<номер>
    // lead=<id|none> action=<attached|dup|no_lead|no_number|error>
    // transcript=<yes|no>".
    private async Task ProcessVoicemailAsync(string extensionId, GetMessageInfoResponse msg)
    {
        var voicemailId = msg.id?.ToString();
        var callerNumber = msg.from?.phoneNumber;
        var callerName = msg.from?.name;
        var hasTranscript = msg.vmTranscriptionStatus == "Completed";

        // Скрытый номер (внешний абонент без Caller ID) — RC не гарантирует
        // from.phoneNumber (см. задачу). Без номера искать сделку нечем —
        // пропускаем, не падая, как и просила задача.
        if (string.IsNullOrWhiteSpace(callerNumber))
        {
            _logger.LogInformation("VM id={Id} number=none lead=none action=no_number transcript={Transcript}",
                voicemailId, hasTranscript ? "yes" : "no");
            return;
        }

        if (_amoService.IsVoicemailProcessed(voicemailId))
        {
            _logger.LogInformation("VM id={Id} number={Number} lead=none action=dup transcript={Transcript}",
                voicemailId, callerNumber, hasTranscript ? "yes" : "no");
            return;
        }

        if (_amoService.IsExpired())
        {
            await _amoService.InitializeAsync();
        }

        IEnumerable<long> candidateLeads;
        try
        {
            candidateLeads = await _amoService.FindLeadByPhoneNumberAsync(callerNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VM id={Id} number={Number} lead=none action=error transcript={Transcript} reason=lead_search_failed",
                voicemailId, callerNumber, hasTranscript ? "yes" : "no");
            return;
        }

        if (candidateLeads == null)
        {
            _logger.LogInformation("VM id={Id} number={Number} lead=none action=no_lead transcript={Transcript}",
                voicemailId, callerNumber, hasTranscript ? "yes" : "no");
            return;
        }

        var targetLeadId = await _amoService.ResolveTargetLeadAsync(candidateLeads);
        if (targetLeadId == null)
        {
            _logger.LogInformation("VM id={Id} number={Number} lead=none action=no_lead transcript={Transcript}",
                voicemailId, callerNumber, hasTranscript ? "yes" : "no");
            return;
        }

        bool noteExists;
        try
        {
            // failClosed=false: тот же выбор, что и у основного поллинга звонков
            // (POLL, не LATE/STARTUP) — узкое окно фетча "по уведомлению",
            // следующего отдельного прохода за этим же сообщением нет, так что
            // fail-open (при сбое считаем, что заметки нет) безопаснее, чем
            // потерять голосовое совсем.
            noteExists = await _amoService.NoteExistsAsync(targetLeadId.Value, "call_in", voicemailId);
        }
        catch (AmoCrmService.NoteExistenceUnknownException ex)
        {
            _logger.LogWarning(ex, "VM id={Id} number={Number} lead={LeadId} action=error transcript={Transcript} reason=note_check_failed",
                voicemailId, callerNumber, targetLeadId, hasTranscript ? "yes" : "no");
            return;
        }

        if (noteExists)
        {
            _amoService.MarkVoicemailProcessed(voicemailId);
            _logger.LogInformation("VM id={Id} number={Number} lead={LeadId} action=dup transcript={Transcript}",
                voicemailId, callerNumber, targetLeadId, hasTranscript ? "yes" : "no");
            return;
        }

        string recordingUrl = null;
        var recordingAttachment = msg.attachments?.FirstOrDefault(a => a.type == "AudioRecording");
        if (recordingAttachment?.id != null)
        {
            var uploadResult = await _amoService.UploadVoicemailRecordingAsync(extensionId, voicemailId, recordingAttachment.id.ToString());
            if (uploadResult.Outcome == RecordingUploadOutcome.Uploaded)
            {
                recordingUrl = uploadResult.Url;
            }
            // NotAvailable/Failed — создаём заметку без ссылки на запись, как
            // и у звонков в основном (не failClosed) пути: узкое окно, следующей
            // попытки специально под это сообщение нет.
        }

        string transcript = null;
        if (hasTranscript)
        {
            var transcriptionAttachment = msg.attachments?.FirstOrDefault(a => a.type == "AudioTranscription");
            if (transcriptionAttachment?.id != null)
            {
                transcript = await _amoService.FetchVoicemailTranscriptAsync(extensionId, voicemailId, transcriptionAttachment.id.ToString());
            }
        }

        // GetMessageInfoResponse.creationTime — string (ISO 8601 с Z, как и у
        // остальных времён RC), не DateTime — см. CLAUDE.md про прошлый баг
        // класса "startTime распарсен не как UTC": парсим явно через
        // DateTimeStyles.AdjustToUniversal|AssumeUniversal, чтобы Z корректно
        // дал Kind=Utc независимо от TimeZoneInfo хоста.
        var messageTimeUtc = DateTime.TryParse(
            msg.creationTime,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsedCreationTime)
            ? parsedCreationTime
            : DateTime.UtcNow;

        bool noteCreated = await _amoService.CreateVoicemailNoteAsync(
            targetLeadId.Value, voicemailId, callerNumber, callerName, messageTimeUtc, transcript, recordingUrl);

        if (!noteCreated)
        {
            _logger.LogWarning("VM id={Id} number={Number} lead={LeadId} action=error transcript={Transcript} reason=note_create_failed",
                voicemailId, callerNumber, targetLeadId, hasTranscript ? "yes" : "no");
            return;
        }

        _amoService.MarkVoicemailProcessed(voicemailId);
        _logger.LogInformation("VM id={Id} number={Number} lead={LeadId} action=attached transcript={Transcript}",
            voicemailId, callerNumber, targetLeadId, hasTranscript ? "yes" : "no");
    }

    // Общая обработка одного SMS-сообщения (входящего или исходящего) —
    // дедупликация, поиск сделки, создание заметки. Используется и instant-путём
    // (входящие, сейчас единственный источник instant-событий), и выборкой через
    // MessageStore().List() (исходящие, за флагом Sms:OutboundEnabled).
    private async Task ProcessSmsMessageAsync(
        string smsId,
        bool isOutbound,
        string toNumber,
        string toName,
        string fromNumber,
        string fromName,
        string smsText)
    {
        // Для исходящего SMS клиент — это получатель (to), а не from (это наш номер).
        string searchNumber = isOutbound ? toNumber : fromNumber;

        _logger.LogInformation("Processing {Direction} SMS {Id}, searching by {SearchNumber}. Text: {SmsText}",
            isOutbound ? "Outbound" : "Inbound", smsId, searchNumber, smsText);

        if (string.IsNullOrWhiteSpace(searchNumber))
        {
            _logger.LogWarning("SMS {Id}: no usable phone number to search, skipping", smsId);
            return;
        }

        if (_amoService.IsSmsProcessed(smsId))
        {
            _logger.LogInformation("SMS {Id} already processed, skipping duplicate", smsId);
            return;
        }

        if (_amoService.IsExpired())
        {
            await _amoService.InitializeAsync();
        }

        // 1. Ищем ID сделок по номеру телефона
        IEnumerable<long> candidateLeads = await _amoService.FindLeadByPhoneNumberAsync(searchNumber);
        if (candidateLeads == null)
        {
            _logger.LogInformation("No leads found for SMS number {Number}", searchNumber);
            return;
        }

        // 2. Выбираем одну сделку: открытую (самую свежую), иначе самую свежую из всех
        var targetLeadId = await _amoService.ResolveTargetLeadAsync(candidateLeads);
        if (targetLeadId == null)
        {
            _logger.LogWarning("Could not resolve a target lead for SMS {Id}", smsId);
            return;
        }

        var noteType = isOutbound ? "sms_out" : "sms_in";

        // 3. Идемпотентность: не создаём заметку повторно, если она уже есть в amoCRM
        if (await _amoService.NoteExistsAsync(targetLeadId.Value, noteType, smsId))
        {
            _logger.LogInformation("SMS {Id} already has a note on lead {LeadId}, skipping", smsId, targetLeadId);
            _amoService.MarkSmsProcessed(smsId);
            return;
        }

        // 4. Формируем текст примечания
        string noteContent =
            $"\nКому: {(string.IsNullOrEmpty(toName) ? "Неизвестен" : toName)} ({toNumber})\n" +
            $"от: {(string.IsNullOrEmpty(fromName) ? "Неизвестен" : fromName)} ({fromNumber})\n" +
            $"Сообщение: {smsText}";

        // 5. Добавляем примечание в карточку сделки
        bool smsNoteCreated = await _amoService.CreateNoteAsync(targetLeadId.Value, noteContent, searchNumber, noteType, smsId);
        if (!smsNoteCreated)
        {
            // Не помечаем обработанным — заметка не создана, следующий
            // вебхук/ретрай от RC (если будет) сможет попробовать снова.
            _logger.LogWarning("SMS {Id} note creation failed, not marking as processed", smsId);
            return;
        }

        _amoService.MarkSmsProcessed(smsId);
    }

    [HttpPost]
    [Route("testWebhook")]
    public async  Task<IActionResult> TestWebHook()
    {
        if (!string.IsNullOrEmpty(BaseTestResponse))
        {
            var notification = System.Text.Json.JsonSerializer.Deserialize<RingCentralNotification>(BaseTestResponse);
            IEnumerable<long> leadsIds = await _amoService.FindLeadByPhoneNumberAsync(notification.Body.From.PhoneNumber);
            
            foreach (long leadId in leadsIds)
            {
                await EnsureAuthorized();

                var getmMssResp = new GetMessageInfoResponse();
                
                var mesaage = await _rc.Restapi().Account().Extension(notification.Body.Owner.ExtensionId)
                    .MessageStore(notification.Body.Id).Get();
                // 2. Формируем текст примечания
                string noteContent =
                    $"\nКому: {notification.Body.To.First().Name} ({notification.Body.To.First().PhoneNumber})\n" +
                    $"от: {(string.IsNullOrEmpty(notification.Body.From.Name) ? "Неизвестен" : notification.Body.From.Name)} ({notification.Body.From.PhoneNumber})\n" +
                    $"Сообщение: {notification.Body.Subject}\n" +
                    $"Вложение: <img scr=\"{notification.Body.Attachments[1].Uri}\" alt=\"attachment\">";

                // 3. Добавляем примечание в карточку сделки
                await _amoService.CreateNoteAsync(leadId, noteContent, notification.Body.From.PhoneNumber, "sms_in", notification.Body.Id);
            }
        }
        else
        {
            return BadRequest();
        }

        return Ok();
    }
    
    [HttpGet]
    [Route("Messages")]// Добавляем GET для случая, если RC использует его для проверки
    public async Task<IActionResult> GetMessages()
    {
        await EnsureAuthorized();
        var messages = await _rc.Restapi().Account().Extension("3017722036").MessageStore().List();
        return Ok(messages); 
    }
    
    [HttpGet]
    [Route("Users")]// Добавляем GET для случая, если RC использует его для проверки
    public async Task<IActionResult> GetUsers()
    {
        await EnsureAuthorized();
        var users = await _rc.Restapi().Account().Extension().List();
        return Ok(users); 
    }
    
    [HttpGet]
    [Route("Subscriptions")]
    public async Task<IActionResult> GetSubscriptions()
    {
        try
        {
            await EnsureAuthorized();
            var resp = await _rc.Restapi().Subscription().List();
            if (resp.records.Length == 0)
            {
                Console.WriteLine("No subscription.");
                return NotFound();
            }
            else
            {
                return Ok(resp.records);
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return StatusCode(500, ex.Message);
        }
    }
    
    [HttpPost]
    [Route("Delete subscription")]
    public async Task<IActionResult> DeleteSub(String subscriptionId)
    {
        try
        {
            await EnsureAuthorized();
            var resp = await _rc.Restapi().Subscription(subscriptionId).Delete();
            Console.WriteLine("Subscription " + subscriptionId + " deleted.");
            return Ok();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return StatusCode(500, ex.Message);
        }
    }
}