using Newtonsoft.Json.Linq;
using RingCentral;
using RingCentral_amoCRM.Helpers;

public class SubscriptionService
{
    private readonly RestClient _rc;
    private readonly ILogger<SubscriptionService> _logger;
    private readonly string _jwt;
    private readonly string WebHookUrl;
    private DateTime _expiresAt;

    public SubscriptionService(RestClient rc, ILogger<SubscriptionService> logger, IConfiguration configuration)
    {
        _rc = rc;
        _logger = logger;
        _jwt = configuration.GetSection("Credentials")["JWT"];
        
        // Логируем наличие JWT токена
        if (string.IsNullOrWhiteSpace(_jwt))
        {
            _logger.LogError("⚠️ JWT token is EMPTY or NULL in configuration!");
        }
        else
        {
            _logger.LogInformation("✅ JWT token loaded successfully (length: {Length} chars)", _jwt.Length);
        }
        
        var expiresAtStr = configuration.GetSection("Credentials")["ExpiresAt"];
        _expiresAt = string.IsNullOrEmpty(expiresAtStr) ? DateTime.UtcNow : DateTime.Parse(expiresAtStr);
        WebHookUrl = configuration.GetSection("Credentials")["RedirectUri"] + "/api/RingCentralWebHook/webhook";
    }
    
    private async Task EnsureAuthorized()
    {
        _logger.LogInformation($"Ensuring RingCentral client is authorized... token exp.{_expiresAt} date now {DateTime.UtcNow}");
        
        // Проверяем наличие JWT токена
        if (string.IsNullOrWhiteSpace(_jwt))
        {
            _logger.LogError("❌ JWT token is empty! Cannot authorize RingCentral.");
            throw new InvalidOperationException("JWT token is not configured.");
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
                _logger.LogError(ex, "❌ Failed to authorize RingCentral with JWT");
                throw;
            }
        }
        else
        {
            _logger.LogInformation("RingCentral token is still valid until {ExpiresAt}", _expiresAt);
        }
    }

    public async Task<string> CreateSmsSubscriptionAsync()
    {
        await EnsureAuthorized();
        var subscriptions = await _rc.Restapi().Subscription().List();
        var users = await _rc.Restapi().Account().Extension().List();
        
        IEnumerable<long?> usersIds = users.records.Select(ext => ext.id).ToList();
        
        _logger.LogInformation("Attempting to create RingCentral WebHook subscription...");
        
        // 1. Настройка фильтров событий.
        // instant-фильтр отдаёт только входящие SMS (RC: default/only direction
        // для /message-store/instant — Inbound, см.
        // developers.ringcentral.com/guide/notifications/event-filters/instant-message).
        // Исходящие идут вторым, отдельным фильтром через обычный (не instant)
        // message-store с direction=Outbound — см.
        // developers.ringcentral.com/guide/notifications/event-filters/message.
        // Это НЕ полная замена instant-фильтра: входящие продолжают идти как раньше,
        // добавляется только вторая пара фильтров на исходящие.
        var eventFilters = usersIds
            .SelectMany(c => new[]
            {
                $"/restapi/v1.0/account/~/extension/{c}/message-store/instant?type=SMS",
                $"/restapi/v1.0/account/~/extension/{c}/message-store?type=SMS&direction=Outbound"
            })
            .ToArray();

        var subscriptionInfo = new CreateSubscriptionRequest
        {
            eventFilters = eventFilters,

            // 2. Настройка способа доставки (WebHook)
            deliveryMode = new NotificationDeliveryModeRequest()
            {
                transportType = "WebHook",
                address = WebHookUrl // Ваш публичный адрес!
            },

            // 3. Срок действия (Максимум 7 дней, устанавливаем 6 дней в секундах)
            expiresIn = 3600 * 24 * 6
        };

        try
        {
            // Важно: сначала создаём НОВУЮ подписку и только при её успехе удаляем
            // старые. Раньше было наоборот (delete всех подписок, потом create) —
            // если POST падал (например, из-за невалидного фильтра), аккаунт
            // оставался вообще без подписки, и входящие SMS переставали приходить
            // до следующего цикла обновления (раз в 5 дней/при рестарте).
            var response = await _rc.Restapi().Subscription().Post(subscriptionInfo);

            if (response == null || string.IsNullOrEmpty(response.id))
            {
                _logger.LogError("❌ RingCentral вернул подписку без id — считаем создание неуспешным, старые подписки (если есть) не трогаем.");
                return null;
            }

            _logger.LogInformation($"✅ Подписка успешно создана для аккаунтов {string.Join(",", usersIds)}!");
            _logger.LogInformation($"ID: {response.id}, Истекает: {response.expirationTime}");

            // Старые подписки удаляем только теперь, когда новая точно создана.
            foreach (var subscription in subscriptions.records)
            {
                if (subscription.id == response.id)
                {
                    continue;
                }

                try
                {
                    await _rc.Restapi().Subscription(subscription.id).Delete();
                }
                catch (Exception deleteEx)
                {
                    // Не критично: лишняя старая подписка продолжит существовать
                    // (и в итоге истечёт сама), но новая уже работает — входящие
                    // и исходящие SMS не пострадают.
                    _logger.LogWarning(deleteEx, "Не удалось удалить старую подписку {SubscriptionId}, оставляем как есть.", subscription.id);
                }
            }

            // Сохраните ID подписки для последующего продления!
            return response.id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Ошибка при создании подписки RingCentral. Убедитесь, что WebHook URL доступен. Старые подписки (если были) не тронуты.");
            return null;
        }
    }

    // Новая периодическая выборка call log
    public async Task PollCallLogsAsync()
    {
        await EnsureAuthorized();

        try
        {
            var callLogs = await _rc.Restapi().Account().CallLog().List();
            // Логируем количество записей (если API возвращает массив 'records')
            if (callLogs != null)
            {
                try
                {
                    var count = callLogs.records?.Length ?? 0;
                    _logger.LogInformation("Fetched call logs: {Count} records", count);
                }
                catch
                {
                    _logger.LogInformation("Fetched call logs (unknown count).");
                }
            }
            else
            {
                _logger.LogInformation("Call log response was null.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while polling call logs from RingCentral API.");
        }
    }
}