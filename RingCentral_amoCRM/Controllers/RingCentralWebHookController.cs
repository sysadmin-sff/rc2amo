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
            var notification = System.Text.Json.JsonSerializer.Deserialize<RingCentralNotification>(notificationJson);

            if (notification?.Body == null)
            {
                _logger.LogWarning("WebHook received (no validation token), but body was empty or invalid JSON. Returning 200 OK.");
                return Ok(); 
            }
            if (notification?.Body != null)
            {
                if ((notification.Body.Direction == "Inbound" || notification.Body.Direction == "Outbound") &&
                    notification.Body.Type == "SMS")
                {
                    string smsText = notification.Body.Subject;
                    string senderName = notification.Body.From.Name;
                    bool isOutbound = notification.Body.Direction == "Outbound";

                    // Для исходящего SMS клиент — это получатель (To), а не From (это наш номер).
                    string searchNumber = isOutbound
                        ? notification.Body.To?.FirstOrDefault()?.PhoneNumber
                        : notification.Body.From?.PhoneNumber;

                    _logger.LogInformation("Received {Direction} SMS, searching by {SearchNumber} ({SenderName}). Text: {SmsText}",
                        notification.Body.Direction, searchNumber, senderName, smsText);

                    if (string.IsNullOrWhiteSpace(searchNumber))
                    {
                        _logger.LogWarning("SMS {Id}: no usable phone number to search, skipping", notification.Body.Id);
                        return Ok();
                    }

                    if (_amoService.IsSmsProcessed(notification.Body.Id))
                    {
                        _logger.LogInformation("SMS {Id} already processed, skipping duplicate", notification.Body.Id);
                        return Ok();
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
                        return Ok();
                    }

                    // 2. Выбираем одну сделку: открытую (самую свежую), иначе самую свежую из всех
                    var targetLeadId = await _amoService.ResolveTargetLeadAsync(candidateLeads);
                    if (targetLeadId == null)
                    {
                        _logger.LogWarning("Could not resolve a target lead for SMS {Id}", notification.Body.Id);
                        return Ok();
                    }

                    var noteType = isOutbound ? "sms_out" : "sms_in";

                    // 3. Идемпотентность: не создаём заметку повторно, если она уже есть в amoCRM
                    if (await _amoService.NoteExistsAsync(targetLeadId.Value, noteType, notification.Body.Id))
                    {
                        _logger.LogInformation("SMS {Id} already has a note on lead {LeadId}, skipping", notification.Body.Id, targetLeadId);
                        _amoService.MarkSmsProcessed(notification.Body.Id);
                        return Ok();
                    }

                    // 4. Формируем текст примечания
                    string noteContent =
                        $"\nКому: {(string.IsNullOrEmpty(notification.Body.To.First().Name) ? "Неизвестен" : notification.Body.To.First().Name)} ({notification.Body.To.First().PhoneNumber})\n" +
                        $"от: {(string.IsNullOrEmpty(notification.Body.From.Name) ? "Неизвестен" : notification.Body.From.Name)} ({notification.Body.From.PhoneNumber})\n" +
                        $"Сообщение: {smsText}";

                    // 5. Добавляем примечание в карточку сделки
                    bool smsNoteCreated = await _amoService.CreateNoteAsync(targetLeadId.Value, noteContent, searchNumber, noteType, notification.Body.Id);
                    if (!smsNoteCreated)
                    {
                        // Не помечаем обработанным — заметка не создана, следующий
                        // вебхук/ретрай от RC (если будет) сможет попробовать снова.
                        _logger.LogWarning("SMS {Id} note creation failed, not marking as processed", notification.Body.Id);
                        return Ok();
                    }

                    _amoService.MarkSmsProcessed(notification.Body.Id);
                    return Created();
                }
            }
        }

        return Ok(); 
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