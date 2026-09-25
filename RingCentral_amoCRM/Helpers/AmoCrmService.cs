using Newtonsoft.Json;
using RingCentral;
using RingCentral_amoCRM.Helpers;
using RingCentral_amoCRM.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static System.Net.WebRequestMethods;
using JsonSerializer = System.Text.Json.JsonSerializer;

public enum CallProcessingResult
{
    Attached,
    Duplicate,
    NoLead,
    NoNumber,
    Error
}

// Различает "у звонка не было записи" (RecordingId пуст — UploadCallRecordingAsync
// вообще не вызывается) от "запись была, но скачать/загрузить не удалось" —
// раньше оба случая давали одинаковый null/"Recording link: not available" в логах,
// что маскировало причину пропажи записи (постоянный сбой RC rate-limit от
// временного "у этого звонка правда нет записи").
public enum RecordingUploadOutcome
{
    Uploaded,
    NotAvailable,   // RC отдал пустой контент — записи нет на стороне RC
    Failed          // скачивание/загрузка не удалось (после повторов) — запись,
                     // предположительно, есть, но сейчас недоступна
}

public readonly struct RecordingUploadResult
{
    public RecordingUploadOutcome Outcome { get; }
    public string Url { get; }

    private RecordingUploadResult(RecordingUploadOutcome outcome, string url)
    {
        Outcome = outcome;
        Url = url;
    }

    public static RecordingUploadResult Uploaded(string url) => new(RecordingUploadOutcome.Uploaded, url);
    public static RecordingUploadResult NotAvailable() => new(RecordingUploadOutcome.NotAvailable, null);
    public static RecordingUploadResult Failed() => new(RecordingUploadOutcome.Failed, null);
}

public class AmoCrmService
{
    private DateTime _tokenExpiration;
    private AmoCrmToken _currentToken;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _redirectUri;
    private readonly string _subdomain;

    private readonly HttpClient _httpClient;
    private readonly ILogger<AmoCrmService> _logger;
    private readonly IConfiguration _configuration;
    private readonly RestClient _rc;

    // Общий на процесс ограничитель heavy-group вызовов RC (call-log, запись
    // разговора) — см. RcHeavyGroupRateLimiter. Используется внутри
    // RunWithRcRetryAsync, единственной точки, откуда идут такие вызовы.
    private readonly RcHeavyGroupRateLimiter _rcLimiter;

    // Быстрый pre-check в памяти для SMS (аналог _processedCallIds у звонков).
    // Живёт здесь, а не в контроллере, т.к. AmoCrmService — singleton, а
    // контроллер создаётся per-request. Источник истины при рестарте —
    // NoteExistsAsync через amoCRM, а не этот набор.
    private readonly HashSet<string> _processedSmsIds = new();

    public bool IsSmsProcessed(string smsId) => !string.IsNullOrEmpty(smsId) && _processedSmsIds.Contains(smsId);

    public void MarkSmsProcessed(string smsId)
    {
        if (!string.IsNullOrEmpty(smsId))
        {
            _processedSmsIds.Add(smsId);
        }
    }

    // Почасовая сводка по WEBHOOK-событиям message-store (не-instant фильтр,
    // Sms:OutboundEnabled=false режим "только лог"). Объём может быть большим
    // (167 доп. исходящих SMS за 3 дня на проде, плюс read/delete/status-change
    // события на те же сообщения) — сводка вместо строки на каждое событие.
    // Считается здесь (singleton), сбрасывается таймером в SmsWebhookSummaryService.
    private int _smsSummaryNewCount;
    private int _smsSummaryUpdatedCount;
    private int _smsSummaryOtherCount;

    // Отдельные счётчики для голосовой почты — своя пара newCount/updatedCount
    // в том же вебхуке (см. HandleMessageStoreChangeAsync), но VoiceMail
    // больше не попадает в otherChanges/_smsSummaryOtherCount (раньше
    // попадал, пока голосовая почта не обрабатывалась отдельно) — без этих
    // счётчиков объём голосовых пропал бы из часовой сводки совсем.
    private int _voicemailSummaryNewCount;
    private int _voicemailSummaryUpdatedCount;

    public void RecordSmsWebhookSummary(int newCount, int updatedCount, int otherCount)
    {
        if (newCount != 0) Interlocked.Add(ref _smsSummaryNewCount, newCount);
        if (updatedCount != 0) Interlocked.Add(ref _smsSummaryUpdatedCount, updatedCount);
        if (otherCount != 0) Interlocked.Add(ref _smsSummaryOtherCount, otherCount);
    }

    public void RecordVoicemailWebhookSummary(int newCount, int updatedCount)
    {
        if (newCount != 0) Interlocked.Add(ref _voicemailSummaryNewCount, newCount);
        if (updatedCount != 0) Interlocked.Add(ref _voicemailSummaryUpdatedCount, updatedCount);
    }

    // Читает и обнуляет накопленные счётчики одним снимком (вызывается таймером
    // раз в час). Interlocked.Exchange, а не read+set — счётчики продолжают
    // получать пополнения от вебхуков всё это время.
    public (int New, int Updated, int Other) FlushSmsWebhookSummary()
    {
        var n = Interlocked.Exchange(ref _smsSummaryNewCount, 0);
        var u = Interlocked.Exchange(ref _smsSummaryUpdatedCount, 0);
        var o = Interlocked.Exchange(ref _smsSummaryOtherCount, 0);
        return (n, u, o);
    }

    public (int New, int Updated) FlushVoicemailWebhookSummary()
    {
        var n = Interlocked.Exchange(ref _voicemailSummaryNewCount, 0);
        var u = Interlocked.Exchange(ref _voicemailSummaryUpdatedCount, 0);
        return (n, u);
    }

    // Курсор "с какого момента ещё не забирали исходящие SMS" на расширение
    // (extensionId из уведомления message-store). По умолчанию — UtcNow на
    // момент первого уведомления по этому расширению, БЕЗ отступа назад:
    // дедупликация исходящих — только _processedSmsIds в памяти (см. выше),
    // при рестарте она пустая, и любой откат курсора назад означает повторную
    // выборку уже обработанных сообщений с риском дублей в сделках. Пропустить
    // немного исходящих SMS в узком окне рестарта безопаснее, чем задублировать
    // заметки в amoCRM. Курсор — тоже только в памяти, тот же класс риска, что
    // и _processedSmsIds (см. риск в описании задачи).
    //
    // НЕ путать с _voicemailCheckpoints ниже, у которого отступ назад ЕСТЬ:
    // разница не случайна, а в разной надёжности дедупликации между sms_out
    // (amoCRM отклоняет params.uniq — только in-memory) и call_in у голосовых
    // (params.uniq принимается — NoteExistsAsync через amoCRM переживает
    // рестарт). Если sms_out когда-нибудь получит поддержку uniq в amoCRM,
    // этот выбор стоит пересмотреть вместе с ним.
    private readonly ConcurrentDictionary<string, DateTime> _outboundSmsCheckpoints = new();

    // Возвращает нижнюю границу выборки для расширения и в том же вызове
    // атомарно продвигает курсор вперёд до notificationLastUpdatedUtc (или
    // UtcNow, если время из уведомления не пришло) — следующий вызов начнёт
    // с этой точки, а не переспросит то же окно повторно. Один AddOrUpdate,
    // а не read+write по отдельности — второе уведомление по тому же
    // extensionId, пришедшее почти одновременно, не должно видеть/затирать
    // промежуточное состояние.
    public DateTime AdvanceOutboundSmsCheckpoint(string extensionId, DateTime? notificationLastUpdatedUtc)
    {
        // RC отдаёт lastUpdated с Z/offset, System.Text.Json должен вернуть Utc,
        // но на всякий случай (см. класс проблем "startTime не как UTC" в
        // истории проекта) — явно нормализуем: значение уходит дальше в
        // ToString("o") для следующего запроса к RC, и Kind.Unspecified/Local
        // там тихо даст неверную границу выборки.
        var newCheckpoint = notificationLastUpdatedUtc.HasValue
            ? (notificationLastUpdatedUtc.Value.Kind == DateTimeKind.Utc
                ? notificationLastUpdatedUtc.Value
                : notificationLastUpdatedUtc.Value.ToUniversalTime())
            : DateTime.UtcNow;
        DateTime previous = default;

        _outboundSmsCheckpoints.AddOrUpdate(
            extensionId,
            addValueFactory: _ =>
            {
                // Первое уведомление по этому расширению: без отступа назад
                // (см. риск дублей в описании задачи) — курсор стартует от
                // текущего момента, а не от newCheckpoint из уведомления.
                previous = DateTime.UtcNow;
                return previous;
            },
            updateValueFactory: (_, existing) =>
            {
                previous = existing;
                return newCheckpoint > existing ? newCheckpoint : existing;
            });

        return previous;
    }

    // Курсор "с какого момента ещё не забирали голосовые сообщения" на
    // расширение — только в памяти, сбрасывается при рестарте (тот же класс
    // риска, что и _outboundSmsCheckpoints ниже). Отдельный словарь, а не
    // переиспользование SMS-курсора: разные типы сообщений, разные окна
    // выборки, самостоятельный сброс.
    //
    // В ОТЛИЧИЕ от SMS-курсора: первое уведомление по расширению стартует
    // не от UtcNow, а от UtcNow − VoicemailFirstCheckpointLookback (см. ниже).
    // Прод-находка: голосовое создаётся за несколько секунд ДО того, как
    // приходит уведомление о нём (webhook event=message-store, vmNew=1) —
    // курсор "с текущей секунды" систематически исключал именно то
    // сообщение, о котором и пришло уведомление
    // (MessageStore.List(VoiceMail) ... returned no records). Задел назад
    // безопасен для голосовых и НЕ безопасен для SMS: у голосовых note_type
    // "call_in" принимает params.uniq, так что NoteExistsAsync — надёжный
    // бэкстоп от дублей независимо от ширины окна выборки; у исходящих SMS
    // (sms_out) amoCRM params.uniq не принимает (см. NoteExistsAsync), дедуп
    // только в памяти (_processedSmsIds) и сбрасывается при рестарте — задел
    // назад там означал бы реальный риск задублировать SMS-заметки после
    // каждого рестарта. Поэтому у AdvanceOutboundSmsCheckpoint ниже задела
    // нет и не должно быть.
    private readonly ConcurrentDictionary<string, DateTime> _voicemailCheckpoints = new();

    private static readonly TimeSpan VoicemailFirstCheckpointLookback = TimeSpan.FromMinutes(5);

    public DateTime AdvanceVoicemailCheckpoint(string extensionId, DateTime? notificationLastUpdatedUtc)
    {
        var newCheckpoint = notificationLastUpdatedUtc.HasValue
            ? (notificationLastUpdatedUtc.Value.Kind == DateTimeKind.Utc
                ? notificationLastUpdatedUtc.Value
                : notificationLastUpdatedUtc.Value.ToUniversalTime())
            : DateTime.UtcNow;
        DateTime previous = default;

        _voicemailCheckpoints.AddOrUpdate(
            extensionId,
            addValueFactory: _ =>
            {
                previous = DateTime.UtcNow - VoicemailFirstCheckpointLookback;
                return previous;
            },
            updateValueFactory: (_, existing) =>
            {
                previous = existing;
                return newCheckpoint > existing ? newCheckpoint : existing;
            });

        return previous;
    }

    // Быстрый pre-check в памяти для голосовых сообщений (аналог
    // _processedSmsIds/_processedCallIds) — источник истины при рестарте
    // остаётся NoteExistsAsync через amoCRM (uniq = id голосового сообщения
    // RC, note_type="call_in" — в отличие от SMS, этот тип принимает uniq,
    // так что дедупликация тут даже надёжнее, чем у SMS).
    private readonly HashSet<string> _processedVoicemailIds = new();

    public bool IsVoicemailProcessed(string voicemailId) =>
        !string.IsNullOrEmpty(voicemailId) && _processedVoicemailIds.Contains(voicemailId);

    public void MarkVoicemailProcessed(string voicemailId)
    {
        if (!string.IsNullOrEmpty(voicemailId))
        {
            _processedVoicemailIds.Add(voicemailId);
        }
    }

    public AmoCrmService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<AmoCrmService> logger, RestClient rc, RcHeavyGroupRateLimiter rcLimiter)
    {
        _configuration = configuration;
        _logger = logger;
        _rc = rc;
        _rcLimiter = rcLimiter;

        _clientId = _configuration["AmoCrm:ClientId"];
        _clientSecret = _configuration["AmoCrm:ClientSecret"];
        _redirectUri = _configuration["AmoCrm:RedirectUri"];
        _subdomain = _configuration["AmoCrm:Subdomain"];

        _httpClient = httpClientFactory.CreateClient("AmoCrmClient");
        // Используем более надежный базовый URL без указания поддомена здесь
        // Поддомен будет добавлен динамически при необходимости
        var baseUrl = $"https://{_subdomain}.amocrm.ru";
        _logger.LogInformation("Configuring AmoCRM HTTP client with base URL: {BaseUrl}", baseUrl);
        _httpClient.BaseAddress = new Uri(baseUrl);

        // Добавляем стандартные заголовки для совместимости
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "RingCentral-AmoCRM-Integration/1.0");
        _httpClient.DefaultRequestHeaders.ConnectionClose = false; // Используем keep-alive
        
        // if AccessToken present in config, set header (will be replaced after refresh)
        var configuredAccess = _configuration["AmoCrm:AccessToken"];
        if (!string.IsNullOrWhiteSpace(configuredAccess))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", configuredAccess);
        }
    }

    public bool IsExpired()
    {
        return DateTime.UtcNow >= _tokenExpiration;
    }

    public async Task InitializeAsync()
    {
        var initialRefreshToken = _configuration["AmoCrm:RefreshToken"];
        if (!string.IsNullOrWhiteSpace(initialRefreshToken))
        {
            await RefreshTokensAsync(initialRefreshToken);
        }
        else
        {
            _logger.LogWarning("No initial amoCRM RefreshToken found in configuration.");
        }
    }

    public async Task<string> GetAccessTokenAsync()
    {
        if (_currentToken == null || _currentToken.IsExpired())
        {
            var refreshToken = _currentToken?.RefreshToken ?? _configuration["AmoCrm:RefreshToken"];
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new InvalidOperationException("No refresh token available to get access token.");

            await RefreshTokensAsync(refreshToken);
        }

        return _currentToken.AccessToken;
    }

    private async Task RefreshTokensAsync(string refreshToken)
    {
        _logger.LogInformation("Attempting to refresh amoCRM tokens...");

        var payload = new Dictionary<string, string>
        {
            { "client_id", _clientId },
            { "client_secret", _clientSecret },
            { "grant_type", "refresh_token" },
            { "refresh_token", refreshToken },
            { "redirect_uri", _redirectUri }
        };

        var content = new FormUrlEncodedContent(payload);
        AmoCrmToken? token = null;

        // Retry logic для обработки временных проблем с SSL/TLS
        int maxRetries = 3;
        int retryDelayMs = 1000;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("Sending token refresh request to {BaseAddress}oauth2/access_token (Attempt {Attempt}/{MaxRetries})", 
                    _httpClient.BaseAddress, attempt, maxRetries);
                
                var response = await _httpClient.PostAsync("/oauth2/access_token", content);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to refresh amoCRM tokens: {StatusCode} {Error}", response.StatusCode, err);
                    throw new Exception($"Failed to refresh amoCRM tokens: {response.StatusCode} - {err}");
                }

                var json = await response.Content.ReadAsStringAsync();
                token = JsonSerializer.Deserialize<AmoCrmToken>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (token == null)
                {
                    _logger.LogError("Failed to deserialize token response: {Json}", json);
                    throw new Exception("Failed to deserialize token response");
                }
                
                token.SetExpiration();
                _currentToken = token;

                // Update HttpClient header
                _httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);

                // Persist new refresh token back to appsettings.json (example)
                try
                {
                    UpdateApp.UpdateAppSetting("AmoCrm:RefreshToken", token.RefreshToken);
                    UpdateApp.UpdateAppSetting("AmoCrm:AccessToken", token.AccessToken);
                    _logger.LogInformation($"AmoCrm token expires at {token.ExpiresAtUtc}");
                    _tokenExpiration = token.ExpiresAtUtc;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not persist refresh token to appsettings.json (permission or environment may block); token will still be used for current run.");
                }

                _logger.LogInformation("amoCRM tokens refreshed successfully.");
                return; // Успех - выходим из метода
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "HTTP request error on attempt {Attempt}/{MaxRetries}. Inner: {InnerMessage}. Retrying in {Delay}ms...", 
                    attempt, maxRetries, ex.InnerException?.Message, retryDelayMs);
                await Task.Delay(retryDelayMs);
                retryDelayMs *= 2; // Exponential backoff
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP request error after {MaxRetries} attempts. Inner exception: {InnerException}", maxRetries, ex.InnerException?.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while refreshing tokens");
            }
        }
    }
    
    public async Task<string> ExchangeCodeForTokensAsync(string code)
    {
        _logger.LogInformation("Attempting to exchange code for amoCRM tokens...");

        var payload = new Dictionary<string, string>
        {
            { "client_id", _clientId },
            { "client_secret", _clientSecret },
            { "grant_type", "authorization_code" },
            { "code", code },
            { "redirect_uri", _redirectUri }
        };

        var content = new FormUrlEncodedContent(payload);
        AmoCrmToken? token = null;

        try
        {
            _logger.LogInformation("Sending code exchange request to {BaseAddress}/oauth2/access_token", _httpClient.BaseAddress);
            var response = await _httpClient.PostAsync("/oauth2/access_token", content);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to exchange code for amoCRM tokens: {StatusCode} {Error}", response.StatusCode, err);
                throw new Exception($"Failed to perform initial amoCRM token exchange: {response.StatusCode} - {err}");
            }

            var json = await response.Content.ReadAsStringAsync();
            token = JsonSerializer.Deserialize<AmoCrmToken>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (token == null)
            {
                _logger.LogError("Failed to deserialize token response: {Json}", json);
                throw new Exception("Failed to deserialize token response");
            }
            
            token.SetExpiration();
            _currentToken = token;

            // Обновляем заголовок HttpClient
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);

            // Сохраняем Refresh Token для последующих запусков
            try
            {
                UpdateApp.UpdateAppSetting("AmoCrm:RefreshToken", token.RefreshToken);
                UpdateApp.UpdateAppSetting("AmoCrm:AccessToken", token.AccessToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not persist initial refresh token. Proceeding with current token.");
            }

            _logger.LogInformation("amoCRM initial authorization successful. Tokens acquired.");
            return token.RefreshToken;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request error while exchanging code. Inner exception: {InnerException}", ex.InnerException?.Message);
            return "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while exchanging code");
            throw;
        }
    }

    // Searches contacts for the phone and tries to return a linked lead id
    public async Task<IEnumerable<long>> FindLeadByPhoneNumberAsync(string phoneNumber)
    {
        var cleanNumber = new string(phoneNumber.Where(char.IsDigit).ToArray());
        var response = await _httpClient.GetAsync($"/api/v4/contacts?query={Uri.EscapeDataString(cleanNumber.Substring(1, cleanNumber.Length - 1))}&with=leads");

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Search request failed: {StatusCode}", response.StatusCode);
            return null;
        }

        if(response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            // Штатный исход: контакта с таким номером в amoCRM ещё нет —
            // не ошибка сервиса, а ожидаемое "нет лида пока", особенно
            // часто для поздней привязки/начального прохода.
            _logger.LogInformation($"No leads found by number {phoneNumber}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();

        var contact = JsonSerializer.Deserialize<AmoCrmContactsResponse>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (contact?.Embedded?.Contacts == null)
        {
            _logger.LogWarning("No lead found for number {Number}.", phoneNumber);
            return null;
        }

        IEnumerable<long> leadsids;
        if (contact.Embedded.Contacts.Any(c => c.Embedded.Leads != null && c.Embedded.Leads.Any()))
        {
            leadsids = contact.Embedded.Contacts
                        .Where(c => c.Embedded.Leads != null)
                        .SelectMany(c => c.Embedded.Leads)
                        .Select(l => l.Id);
            return leadsids;
        }

        _logger.LogWarning("No lead found for number {Number}.", phoneNumber);
        return null;
    }

    private const long ClosedWonStatusId = 142;
    private const long ClosedLostStatusId = 143;

    // Значения RingCentral CallLogRecord.result, означающие, что разговор не
    // состоялся (звонок пропущен/не принят/ушёл на автоответчик и т.п.).
    private static readonly HashSet<string> MissedCallResults = new(StringComparer.OrdinalIgnoreCase)
    {
        "Missed", "No Answer", "Voicemail", "Rejected", "Busy", "Abandoned"
    };

    // Picks a single target lead out of a pool of candidates: prefers the most
    // recently updated OPEN lead (status not closed-won/closed-lost); if none
    // are open, falls back to the most recently updated lead overall.
    public async Task<long?> ResolveTargetLeadAsync(IEnumerable<long> candidateLeadIds)
    {
        var ids = candidateLeadIds?.Distinct().ToList();
        if (ids == null || ids.Count == 0)
        {
            return null;
        }

        if (ids.Count == 1)
        {
            return ids[0];
        }

        var query = string.Join("&", ids.Select(id => $"filter[id][]={id}"));
        var response = await _httpClient.GetAsync($"/api/v4/leads?{query}");

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Lead resolution request failed: {StatusCode}", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var leadsResponse = JsonSerializer.Deserialize<AmoCrmLeadsListResponse>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var leads = leadsResponse?.Embedded?.Leads;
        if (leads == null || leads.Count == 0)
        {
            _logger.LogWarning("Lead resolution: none of the candidate leads {Ids} were returned by amoCRM", string.Join(",", ids));
            return null;
        }

        var openLeads = leads.Where(l => l.StatusId != ClosedWonStatusId && l.StatusId != ClosedLostStatusId).ToList();
        var pool = openLeads.Count > 0 ? openLeads : leads;

        return pool.OrderByDescending(l => l.UpdatedAt).First().Id;
    }

    private const int NoteExistsMaxPages = 5;
    private const int UpdatedEntitiesPageSize = 250;
    private const int UpdatedEntitiesMaxPages = 50;
    private const int NoteExistsRetryMaxAttempts = 3;
    private static readonly TimeSpan[] NoteExistsRetryDelays =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)
    };

    // Оба heavy-group эндпоинта RC, которые мы дёргаем —
    // /restapi/v1.0/account/~/call-log (журнал звонков, все три пути: POLL,
    // STARTUP, LATE) и /restapi/v1.0/account/~/recording/{id}/content (запись) —
    // упираются в один и тот же жёсткий rate limit. На проде наблюдалось: при
    // догоне (STARTUP, широкое окно) — до 18 "Request rate exceeded" (CMN-301)
    // подряд на recording с Retry-After: 60; после того как на recording добавили
    // retry, лимит стал доставаться call-log — все три сервиса стартуют почти
    // одновременно и втроём выедают лимит на первом же запросе журнала звонков.
    // RingCentral.Net (SDK) бросает RestException на любой не-2xx ответ, но не
    // отдаёт статус-код/заголовки через публичный API (единственный конструктор
    // кладёт HttpResponseMessage в приватное поле; публичных Response/StatusCode/
    // Headers нет) — полагаться на reflection в приватные поля SDK означало бы
    // завязаться на недокументированную деталь реализации конкретной версии
    // пакета. Вместо этого достаём Retry-After из ex.Message: конструктор
    // RestException форматирует его через HttpResponseMessage.ToString(), который
    // (проверено эмпирически на .NET 8) включает блок "Headers: { ... }" со
    // строкой "Retry-After: <секунды>" как есть — это то же текстовое
    // представление, из которого читает Message, а не отдельный недокументированный
    // контракт. Если распознать не удалось (SDK сменит формат ex.Message, или
    // заголовка нет) — используем зафиксированное на проде значение 60с как
    // безопасный запасной вариант.
    private const int RcRetryMaxAttempts = 3;
    private static readonly TimeSpan RcRetryFallbackDelay = TimeSpan.FromSeconds(60);
    private static readonly System.Text.RegularExpressions.Regex RetryAfterPattern =
        new(@"Retry-After:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static TimeSpan GetRetryDelay(RestException ex)
    {
        var match = RetryAfterPattern.Match(ex.Message ?? "");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return RcRetryFallbackDelay;
    }

    // Пауза между скачиваниями записей в пакетных путях (STARTUP catch-up,
    // LateAttach), чтобы самим не создавать всплеск запросов к heavy-group
    // эндпоинту записи — независимо от того, сработал ли retry выше.
    public static readonly TimeSpan RecordingDownloadBatchPause = TimeSpan.FromSeconds(2);

    // Брошено NoteExistsAsync(failClosed: true), когда мы не смогли достоверно
    // проверить, есть ли уже заметка (сбой запроса, не-успешный статус после
    // повторов, или исчерпан лимит страниц). Вызывающий код должен трактовать
    // это как ошибку обработки звонка, а не как "заметки нет".
    public class NoteExistenceUnknownException : Exception
    {
        public NoteExistenceUnknownException(string message, Exception? inner = null)
            : base(message, inner) { }
    }

    // Повторяет запрос до NoteExistsRetryMaxAttempts раз на HTTP 429, выдерживая
    // паузу по Retry-After (секунды или HTTP-date), а если заголовка нет —
    // 1-2-4 секунды. Возвращает последний ответ как есть (включая 429/5xx) —
    // вызывающий код сам решает, что делать с неуспехом (fail-open/fail-closed).
    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<Task<HttpResponseMessage>> sendRequest)
    {
        HttpResponseMessage response = null;

        for (int attempt = 1; attempt <= NoteExistsRetryMaxAttempts; attempt++)
        {
            response = await sendRequest();

            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt == NoteExistsRetryMaxAttempts)
            {
                return response;
            }

            var delay = NoteExistsRetryDelays[attempt - 1];
            if (response.Headers.RetryAfter != null)
            {
                if (response.Headers.RetryAfter.Delta.HasValue)
                {
                    delay = response.Headers.RetryAfter.Delta.Value;
                }
                else if (response.Headers.RetryAfter.Date.HasValue)
                {
                    var untilDate = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                    if (untilDate > TimeSpan.Zero)
                    {
                        delay = untilDate;
                    }
                }
            }

            _logger.LogWarning("amoCRM returned 429, retrying in {Delay}s (attempt {Attempt}/{Max})",
                delay.TotalSeconds, attempt, NoteExistsRetryMaxAttempts);
            await Task.Delay(delay);
        }

        return response;
    }

    // Checks whether a lead already has a note of the given type carrying
    // params.uniq == uniqValue. amoCRM's notes endpoint has no server-side
    // filter on custom params fields, so this scans notes filtered by type.
    //
    // failClosed управляет поведением при невозможности достоверно проверить:
    // - false (основной поллинг, звонок только что случился): fail-open,
    //   возвращает false ("заметки нет") — редкий дубль при сбое API лучше,
    //   чем потерянный звонок в узком 5-минутном окне без повторных попыток.
    // - true (поздняя привязка, догон при старте): fail-closed, бросает
    //   NoteExistenceUnknownException — у этих путей есть следующий цикл
    //   опроса, так что безопаснее не создавать заметку "вслепую", когда
    //   мы не смогли проверить, а вместо этого попробовать снова позже.
    //
    // notEarlierThanUtc — необязательная оптимизация: если передано, в запрос
    // добавляется filter[updated_at][from] (минус час запаса), чтобы отсечь
    // заметки, обновлённые раньше звонка, и снизить число просматриваемых
    // страниц. ВНИМАНИЕ: предположение "у заметки с явно заданным created_at
    // updated_at не может быть раньше времени звонка" НЕ подтверждено
    // документацией amoCRM v4 — created_at как принимаемое поле при создании
    // заметки вообще не задокументирован официально (см. комментарий в
    // CreateCallNoteAsync). Поэтому фильтр применяется ТОЛЬКО при
    // failClosed=false (основной поллинг): там ошибочно неверный результат
    // означает редкий дубль — тот же риск, что и без фильтра. На fail-closed
    // путях (LATE/STARTUP) фильтр НЕ применяется: если amoCRM отфильтрует
    // существующую заметку на своей стороне (вернёт 200 OK с пустым/неполным
    // списком вместо ошибки), NoteExistenceUnknownException не сработает и
    // мы молча создадим дубль — ровно то, для защиты от чего fail-closed
    // существует. Требует эмпирической проверки на реальном amoCRM-аккаунте;
    // включить и для fail-closed путей можно только после подтверждения.
    public async Task<bool> NoteExistsAsync(
        long leadId,
        string noteType,
        string uniqValue,
        bool failClosed = false,
        DateTime? notEarlierThanUtc = null)
    {
        // sms_in/sms_out notes never carry params.uniq (amoCRM rejects it with
        // 400 FieldNotExpected on creation — see CreateNoteAsync), so scanning
        // for a match would always come back empty. Skip the amoCRM round-trip
        // entirely; SMS dedup relies solely on the in-memory _processedSmsIds set.
        if (noteType == "sms_in" || noteType == "sms_out")
        {
            return false;
        }

        string updatedAtFilter = "";
        if (notEarlierThanUtc.HasValue && !failClosed)
        {
            var sinceUnix = ((DateTimeOffset)DateTime.SpecifyKind(notEarlierThanUtc.Value, DateTimeKind.Utc))
                .AddHours(-1)
                .ToUnixTimeSeconds();
            updatedAtFilter = $"&filter[updated_at][from]={sinceUnix}";
        }

        for (int page = 1; page <= NoteExistsMaxPages; page++)
        {
            HttpResponseMessage response;
            try
            {
                response = await SendWithRetryAsync(() => _httpClient.GetAsync(
                    $"/api/v4/leads/{leadId}/notes?filter[note_type]={Uri.EscapeDataString(noteType)}{updatedAtFilter}&limit=250&page={page}&order[updated_at]=desc"));
            }
            catch (Exception ex)
            {
                if (failClosed)
                {
                    throw new NoteExistenceUnknownException(
                        $"NoteExistsAsync: request to amoCRM failed for lead {leadId} (fail-closed)", ex);
                }

                // Fail-open: сбой запроса к amoCRM не должен блокировать создание заметки.
                // Редкий дубль при сбое API — меньшее зло, чем потерянный звонок/SMS,
                // особенно с учётом узкого окна свежести и отсутствия повторных попыток.
                _logger.LogWarning(ex, "NoteExistsAsync: request to amoCRM failed for lead {LeadId}, assuming note does not exist (fail-open)", leadId);
                return false;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                if (failClosed)
                {
                    throw new NoteExistenceUnknownException(
                        $"NoteExistsAsync: amoCRM returned {response.StatusCode} for lead {leadId} (fail-closed)");
                }

                // Fail-open здесь же: 429/5xx/прочие ошибки amoCRM трактуются как
                // "заметки не нашли", а не как "заметка точно есть".
                _logger.LogWarning("NoteExistsAsync: amoCRM returned {StatusCode} for lead {LeadId}, assuming note does not exist (fail-open)", response.StatusCode, leadId);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            var notesResponse = JsonSerializer.Deserialize<AmoCrmNotesListResponse>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var notes = notesResponse?.Embedded?.Notes;
            if (notes == null || notes.Count == 0)
            {
                return false;
            }

            foreach (var note in notes)
            {
                if (note.Params.ValueKind == JsonValueKind.Object &&
                    note.Params.TryGetProperty("uniq", out var uniqProp) &&
                    uniqProp.ValueKind == JsonValueKind.String &&
                    uniqProp.GetString() == uniqValue)
                {
                    return true;
                }
            }

            if (notes.Count < 250)
            {
                return false;
            }
        }

        if (failClosed)
        {
            throw new NoteExistenceUnknownException(
                $"NoteExistsAsync: exhausted {NoteExistsMaxPages} pages for lead {leadId} without a definitive answer (fail-closed)");
        }

        return false;
    }

    // Контакты, изменённые с sinceUtc (включительно). with=leads — чтобы
    // не делать отдельный запрос за связанными сделками для каждого контакта.
    public async Task<List<AmoCrmContact>> GetUpdatedContactsAsync(DateTime sinceUtc)
    {
        var result = new List<AmoCrmContact>();
        var sinceUnix = ((DateTimeOffset)DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        for (int page = 1; page <= UpdatedEntitiesMaxPages; page++)
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(
                    $"/api/v4/contacts?filter[updated_at][from]={sinceUnix}&with=leads&limit={UpdatedEntitiesPageSize}&page={page}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetUpdatedContactsAsync: request failed on page {Page}", page);
                throw;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                break;
            }

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogError("GetUpdatedContactsAsync: amoCRM returned {StatusCode} {Error}", response.StatusCode, err);
                throw new Exception($"GetUpdatedContactsAsync failed: {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var parsed = JsonSerializer.Deserialize<AmoCrmContactsResponse>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var contacts = parsed?.Embedded?.Contacts;
            if (contacts == null || contacts.Count == 0)
            {
                break;
            }

            result.AddRange(contacts);

            if (contacts.Count < UpdatedEntitiesPageSize)
            {
                break;
            }
        }

        return result;
    }

    // Дотягивает custom_fields_values (включая PHONE) для контактов,
    // известных только по id — например, тех, что пришли через
    // GetUpdatedLeadsWithContactsAsync, где /leads?with=contacts не отдаёт
    // custom-поля контакта, только id/name.
    public async Task<List<AmoCrmContact>> GetContactsByIdsAsync(IReadOnlyCollection<long> ids)
    {
        var result = new List<AmoCrmContact>();
        if (ids == null || ids.Count == 0)
        {
            return result;
        }

        // amoCRM ограничивает длину query string; для планового объёма (сотни
        // изменённых сделок за цикл при импорте) режем на батчи по 100 id.
        const int batchSize = 100;
        var idsList = ids.Distinct().ToList();

        for (int offset = 0; offset < idsList.Count; offset += batchSize)
        {
            var batch = idsList.Skip(offset).Take(batchSize);
            var query = string.Join("&", batch.Select(id => $"filter[id][]={id}"));

            var response = await _httpClient.GetAsync($"/api/v4/contacts?{query}&limit={batchSize}");

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogError("GetContactsByIdsAsync: amoCRM returned {StatusCode} {Error}", response.StatusCode, err);
                throw new Exception($"GetContactsByIdsAsync failed: {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var parsed = JsonSerializer.Deserialize<AmoCrmContactsResponse>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed?.Embedded?.Contacts != null)
            {
                result.AddRange(parsed.Embedded.Contacts);
            }
        }

        return result;
    }

    // Сделки, изменённые с sinceUtc, вместе с привязанными контактами.
    // Нужны отдельно от GetUpdatedContactsAsync: если к существующему
    // (не изменившемуся) контакту добавили сделку, сам контакт может не
    // попасть в выборку по updated_at, а вот сделка — попадёт всегда,
    // т.к. она только что создана/обновлена.
    public async Task<List<AmoCrmLeadDetail>> GetUpdatedLeadsWithContactsAsync(DateTime sinceUtc)
    {
        var result = new List<AmoCrmLeadDetail>();
        var sinceUnix = ((DateTimeOffset)DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        for (int page = 1; page <= UpdatedEntitiesMaxPages; page++)
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(
                    $"/api/v4/leads?filter[updated_at][from]={sinceUnix}&with=contacts&limit={UpdatedEntitiesPageSize}&page={page}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetUpdatedLeadsWithContactsAsync: request failed on page {Page}", page);
                throw;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                break;
            }

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                _logger.LogError("GetUpdatedLeadsWithContactsAsync: amoCRM returned {StatusCode} {Error}", response.StatusCode, err);
                throw new Exception($"GetUpdatedLeadsWithContactsAsync failed: {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var parsed = JsonSerializer.Deserialize<AmoCrmLeadsListResponse>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var leads = parsed?.Embedded?.Leads;
            if (leads == null || leads.Count == 0)
            {
                break;
            }

            result.AddRange(leads);

            if (leads.Count < UpdatedEntitiesPageSize)
            {
                break;
            }
        }

        return result;
    }

    // Последние 10 цифр — минимальный общий знаменатель между форматами
    // amoCRM (+1XXXXXXXXXX/XXXXXXXXXX) и RingCentral (phoneNumber в call log).
    // internal — переиспользуется в LateAttachService, чтобы не дублировать
    // ту же логику.
    internal static string NormalizePhone(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length >= 10 ? digits[^10..] : null;
    }

    // Собирает нормализованные телефоны контактов из значений custom-поля
    // PHONE (field_code == "PHONE"). Multitext-поле — берём ВСЕ значения,
    // не только первое: у контакта может быть несколько номеров, и звонок
    // мог прийти с любого из них.
    public static HashSet<string> ExtractNormalizedPhones(IEnumerable<AmoCrmContact> contacts)
    {
        var phones = new HashSet<string>();
        if (contacts == null)
        {
            return phones;
        }

        foreach (var contact in contacts)
        {
            if (contact.CustomFieldsValues == null)
            {
                continue;
            }

            foreach (var field in contact.CustomFieldsValues)
            {
                if (field.FieldCode != "PHONE" || field.Values == null)
                {
                    continue;
                }

                foreach (var value in field.Values)
                {
                    var normalized = NormalizePhone(value.Value);
                    if (normalized != null)
                    {
                        phones.Add(normalized);
                    }
                }
            }
        }

        return phones;
    }

    // Create an SMS note attached to a lead. noteType: "sms_in" or "sms_out".
    // amoCRM rejects params.uniq for sms_in/sms_out (400 FieldNotExpected) —
    // unlike call_in/call_out, this note type's params schema does not include
    // it, so uniqId is NOT sent to amoCRM. It is only used for the log line
    // below (to cross-reference against RingCentral's message id) and by the
    // caller for the in-memory _processedSmsIds dedup check.
    // Возвращает true только при 2xx-ответе amoCRM. Вызывающий код обязан
    // проверить результат перед тем, как помечать SMS обработанным — иначе
    // не-2xx (например 400 из-за некорректного payload) молча теряет SMS.
    public async Task<bool> CreateNoteAsync(long leadId, string noteText, string phone, string noteType, string uniqId)
    {
        const string entityType = "leads";

        const long responsibleUserId = 8644141;

        var textPrefix = noteType == "sms_out" ? "Исходящее SMS к" : "Входящее SMS от";

        var rootJsonArray = new JsonArray
        {
            new JsonObject
            {
                ["note_type"] = noteType,
                ["params"] = new JsonObject
                {
                    ["text"] = $"{textPrefix} {phone}: {noteText}",
                    ["phone"] = $"{phone}"
                }
            }
        };

        var content = new StringContent(rootJsonArray.ToJsonString(), Encoding.UTF8, "application/json");
        var url = $"/api/v4/{entityType}/{leadId}/notes";
        var resp = await _httpClient.PostAsync(url, content);
        if (resp.IsSuccessStatusCode)
        {
            _logger.LogInformation("Note added to amoCRM (Lead ID: {LeadId}) successfully. RingCentral message id: {MessageId}", leadId, uniqId);
            return true;
        }

        var err = await resp.Content.ReadAsStringAsync();
        _logger.LogError("Failed to add note: {StatusCode} {ErrorBody}. RingCentral message id: {MessageId}", resp.StatusCode, err, uniqId);
        return false;
    }

    // Общий повтор для любого вызова RingCentral.Net SDK, бросающего RestException
    // на не-2xx (CallLog().List(...), Recording(...).Content().Get(), ...). Оба
    // heavy-group эндпоинта — журнал звонков и запись разговора — упираются в один
    // и тот же тип лимита RC (CMN-301 "Request rate exceeded", Retry-After), так
    // что ретрай-логика одна на оба случая; opLabel уходит только в лог, чтобы
    // различать, какой именно вызов повторяется.
    //
    // Каждая попытка проходит через RcHeavyGroupRateLimiter — единственный на
    // процесс гейт для heavy-group вызовов. Без него разнесение стартов сервисов
    // по времени (Startup:CatchUpDelaySeconds/LateAttachDelaySeconds) не спасало
    // на проде: лимит heavy-group общий на аккаунт, а не по одному на сервис, и
    // сервис, узнавший о 429 первым, ничего не сообщал остальным — те продолжали
    // ходить в API и получали свои 429 независимо, потребляя тот же бюджет.
    // Теперь Retry-After с любого 429 становится общим "не раньше чем" для всех
    // следующих heavy-group вызовов процесса, откуда бы они ни шли.
    public async Task<T> RunWithRcRetryAsync<T>(Func<Task<T>> action, string opLabel)
    {
        for (int attempt = 1; attempt <= RcRetryMaxAttempts; attempt++)
        {
            using (await _rcLimiter.AcquireAsync())
            {
                try
                {
                    return await action();
                }
                catch (RestException ex) when (IsRateLimitError(ex) && attempt < RcRetryMaxAttempts)
                {
                    var delay = GetRetryDelay(ex);
                    _rcLimiter.ReportRateLimited(delay);
                    _logger.LogWarning(
                        "{OpLabel}: RingCentral rate limit (CMN-301), retrying in {Delay}s (attempt {Attempt}/{Max})",
                        opLabel, delay.TotalSeconds, attempt, RcRetryMaxAttempts);
                }
            }
        }

        // Последняя попытка (attempt == RcRetryMaxAttempts) не попадает под
        // when-условие catch выше и бросает исходное исключение наружу сама —
        // сюда управление не доходит.
        throw new InvalidOperationException($"RunWithRcRetryAsync({opLabel}): retry loop exited without a result");
    }

    // Скачивает запись из RingCentral с повтором на rate limit (CMN-301 "Request
    // rate exceeded"), уважая Retry-After (см. GetRetryDelay). Максимум
    // RcRetryMaxAttempts попыток.
    private Task<byte[]> DownloadRecordingWithRetryAsync(string recordingId) =>
        RunWithRcRetryAsync(
            () => _rc.Restapi().Account().Recording(recordingId).Content().Get(),
            $"Recording {recordingId}");

    // Повтор на rate limit БЕЗ RcHeavyGroupRateLimiter — тот гейт настроен под
    // heavy-group бюджет (10 запросов/мин на аккаунт, общий с call-log и
    // recording/content, см. RcHeavyGroupRateLimiter). Список сообщений
    // message-store — не heavy-group эндпоинт: по данным RC (и по объёму вызовов
    // здесь — один на новое исходящее SMS, не polling) заводить его в общий с
    // call-log/recording гейт не нужно и не должно тормозить оба независимых
    // потока друг другом. Если это предположение окажется неверным на практике,
    // повтор с уважением Retry-After (тот же GetRetryDelay/IsRateLimitError, что
    // и у heavy-group) всё равно не даст тихо потерять сообщение — просто без
    // координации с call-log/recording.
    private async Task<T> RunWithLightRetryAsync<T>(Func<Task<T>> action, string opLabel)
    {
        for (int attempt = 1; attempt <= RcRetryMaxAttempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (RestException ex) when (IsRateLimitError(ex) && attempt < RcRetryMaxAttempts)
            {
                var delay = GetRetryDelay(ex);
                _logger.LogWarning(
                    "{OpLabel}: RingCentral rate limit, retrying in {Delay}s (attempt {Attempt}/{Max})",
                    opLabel, delay.TotalSeconds, attempt, RcRetryMaxAttempts);
                await Task.Delay(delay);
            }
        }

        throw new InvalidOperationException($"RunWithLightRetryAsync({opLabel}): retry loop exited without a result");
    }

    // Забирает новые исходящие SMS для расширения начиная с sinceUtc (не
    // включительно) — используется только когда Sms:OutboundEnabled=true и
    // не-instant вебхук сообщил newCount>0 для SMS на этом расширении.
    public Task<GetMessageList> FetchOutboundSmsAsync(string extensionId, DateTime sinceUtc) =>
        RunWithLightRetryAsync(
            () => _rc.Restapi().Account().Extension(extensionId).MessageStore().List(new ListMessagesParameters
            {
                direction = new[] { "Outbound" },
                messageType = new[] { "SMS" },
                dateFrom = sinceUtc.ToString("o")
            }),
            $"MessageStore.List(ext={extensionId})");

    // Забирает новые голосовые сообщения для расширения начиная с sinceUtc —
    // тот же паттерн, что и FetchOutboundSmsAsync (не-instant вебхук сообщил
    // newCount>0 для VoiceMail на этом расширении, дальше дозапрашиваем сами
    // сообщения через MessageStore().List()). direction не указываем —
    // голосовая почта у RC всегда Inbound (пропущенный/переведённый на
    // автоответчик звонок), фильтр по direction не нужен и не документирован
    // как поддерживаемый для VoiceMail.
    // "VoiceMail" (заглавная M) — проверено прямыми запросами к RC (см.
    // CLAUDE.md). НЕ путать с eventFilter подписки (SubscriptionService),
    // который для того же понятия требует "Voicemail" (строчная m) — разный
    // регистр в разных частях API RC, не опечатка ни там, ни тут.
    public Task<GetMessageList> FetchVoicemailsAsync(string extensionId, DateTime sinceUtc) =>
        RunWithLightRetryAsync(
            () => _rc.Restapi().Account().Extension(extensionId).MessageStore().List(new ListMessagesParameters
            {
                messageType = new[] { "VoiceMail" },
                dateFrom = sinceUtc.ToString("o")
            }),
            $"MessageStore.List(ext={extensionId}, VoiceMail)");

    private static bool IsRateLimitError(RestException ex) =>
        ex.Message.Contains("429") || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);

    // Публичная обёртка для вызывающего кода (CallLogPollingService,
    // LateAttachService): после того как RunWithRcRetryAsync исчерпал попытки
    // и пробросил исключение, это позволяет отличить в логе "RC так и не ответил
    // из-за rate limit" от прочих сбоев (сеть, авторизация и т.п.) — CMN-301 после
    // ретраев значит, что лимит выедается быстрее, чем мы успеваем его отпустить
    // (см. RunWithRcRetryAsync), а не разовый временный сбой.
    public static bool IsRcRateLimitException(Exception ex) =>
        ex is RestException restEx && IsRateLimitError(restEx);

    // Возвращает исход отдельно от URL: RecordingUploadOutcome.NotAvailable —
    // у звонка на стороне RC нет записи (пустой контент); .Failed — скачивание
    // или загрузка не удались (в т.ч. после исчерпания повторов на rate limit) —
    // запись, предположительно, есть, но сейчас недоступна. Раньше оба случая
    // возвращали null неразличимо, и лог показывал одинаковое "Recording link:
    // not available" для обеих ситуаций.
    public async Task<RecordingUploadResult> UploadCallRecordingAsync(string recordingId, string callId)
    {
        try
        {
            _logger.LogInformation("Downloading recording {RecordingId} from RingCentral...", recordingId);

            byte[] recordingContent;
            try
            {
                recordingContent = await DownloadRecordingWithRetryAsync(recordingId);
            }
            catch (RestException ex)
            {
                _logger.LogError(ex, "Recording {RecordingId}: download failed after retries", recordingId);
                return RecordingUploadResult.Failed();
            }

            if (recordingContent == null || recordingContent.Length == 0)
            {
                _logger.LogWarning("Recording {RecordingId} is empty or not available", recordingId);
                return RecordingUploadResult.NotAvailable();
            }

            return await UploadBytesToAmoDriveAsync(recordingContent, $"call_recording_{callId}.mp3", "audio/mpeg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading recording {RecordingId} to amoCRM drive", recordingId);
            return RecordingUploadResult.Failed();
        }
    }

    // Общая часть UploadCallRecordingAsync/UploadVoicemailRecordingAsync:
    // байты уже скачаны с RC (это специфично для звонка/голосового и
    // остаётся в каждом из них), дальше — одинаковая загрузка в amoCRM Drive
    // (сессия → части → download-ссылка). Извлечено без изменения логики —
    // тело метода перенесено как есть из UploadCallRecordingAsync.
    private async Task<RecordingUploadResult> UploadBytesToAmoDriveAsync(byte[] content, string fileName, string contentType)
    {
        _logger.LogInformation("Content downloaded, size: {Size} bytes. Uploading to amoCRM drive...", content.Length);

        // Получаем drive_url для текущего аккаунта
        var accountResponse = await _httpClient.GetAsync("/api/v4/account?with=drive_url");
        if (!accountResponse.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to get account drive_url: {StatusCode}", accountResponse.StatusCode);
            return RecordingUploadResult.Failed();
        }

        var accountJson = await accountResponse.Content.ReadAsStringAsync();
        var accountData = JsonSerializer.Deserialize<JsonElement>(accountJson);

        string driveUrl = accountData.GetProperty("drive_url").GetString();
        if (string.IsNullOrEmpty(driveUrl))
        {
            _logger.LogError("Drive URL is empty in account response");
            return RecordingUploadResult.Failed();
        }

        _logger.LogInformation("Got drive URL: {DriveUrl}", driveUrl);

        // Шаг 1: Создаем сессию загрузки
        var sessionPayload = new
        {
            file_name = fileName,
            file_size = content.Length,
            content_type = contentType
        };

        var sessionContent = new StringContent(
            JsonSerializer.Serialize(sessionPayload),
            Encoding.UTF8,
            "application/json");

        using var driveClient = new HttpClient();
        driveClient.Timeout = TimeSpan.FromMinutes(5);
        driveClient.DefaultRequestHeaders.Authorization = _httpClient.DefaultRequestHeaders.Authorization;

        var sessionResponse = await driveClient.PostAsync($"{driveUrl}/v1.0/sessions", sessionContent);

        if (!sessionResponse.IsSuccessStatusCode)
        {
            var error = await sessionResponse.Content.ReadAsStringAsync();
            _logger.LogError("Failed to create upload session: {StatusCode} {Error}", sessionResponse.StatusCode, error);
            return RecordingUploadResult.Failed();
        }

        var sessionJson = await sessionResponse.Content.ReadAsStringAsync();
        var session = JsonSerializer.Deserialize<JsonElement>(sessionJson);

        string uploadUrl = session.GetProperty("upload_url").GetString();
        int maxPartSize = session.GetProperty("max_part_size").GetInt32();
        string fileUuid = null;

        _logger.LogInformation("Upload session created. Max part size: {MaxPartSize}", maxPartSize);

        // Шаг 2: Загружаем файл по частям
        int offset = 0;
        string nextUrl = uploadUrl;

        while (offset < content.Length)
        {
            int partSize = Math.Min(maxPartSize, content.Length - offset);
            var partContent = new ByteArrayContent(content, offset, partSize);
            partContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

            _logger.LogInformation("Uploading part: offset={Offset}, size={PartSize}", offset, partSize);

            var uploadResponse = await driveClient.PostAsync(nextUrl, partContent);

            if (!uploadResponse.IsSuccessStatusCode)
            {
                var error = await uploadResponse.Content.ReadAsStringAsync();
                _logger.LogError("Failed to upload file part: {StatusCode} {Error}", uploadResponse.StatusCode, error);
                return RecordingUploadResult.Failed();
            }

            var uploadJson = await uploadResponse.Content.ReadAsStringAsync();
            var uploadResult = JsonSerializer.Deserialize<JsonElement>(uploadJson);

            // Если есть next_url - продолжаем загрузку
            if (uploadResult.TryGetProperty("next_url", out var nextUrlElement))
            {
                nextUrl = nextUrlElement.GetString();
                offset += partSize;
            }
            else
            {
                // Это последняя часть - получаем UUID файла и download link
                fileUuid = uploadResult.GetProperty("uuid").GetString();

                // Получаем ссылку на скачивание из _links
                if (uploadResult.TryGetProperty("_links", out var links) &&
                    links.TryGetProperty("download", out var downloadLink) &&
                    downloadLink.TryGetProperty("href", out var downloadHref))
                {
                    var downloadUrl = downloadHref.GetString();
                    _logger.LogInformation("✅ File uploaded successfully. UUID: {FileUuid}, Download URL: {DownloadUrl}", fileUuid, downloadUrl);
                    return RecordingUploadResult.Uploaded(downloadUrl);
                }

                _logger.LogInformation("✅ File uploaded successfully. UUID: {FileUuid}", fileUuid);
                break;
            }
        }

        if (string.IsNullOrEmpty(fileUuid))
        {
            _logger.LogError("Failed to get file UUID after upload");
            return RecordingUploadResult.Failed();
        }

        // Если не получили download URL из ответа, формируем его вручную
        var finalDownloadUrl = $"{driveUrl}/download/{fileUuid}";
        _logger.LogInformation("✅ Recording uploaded to amoCRM drive: {Url}", finalDownloadUrl);

        return RecordingUploadResult.Uploaded(finalDownloadUrl);
    }

    // Скачивает содержимое вложения голосового сообщения (аудио, вложение
    // типа AudioRecording) и загружает его в amoCRM Drive — переиспользует
    // ту же сессию/цикл multi-part upload, что и UploadCallRecordingAsync
    // для записей звонков (проверено по RingCentral.Net.dll: оба метода,
    // Account().Recording(id).Content().Get() и
    // Account().Extension(extId).MessageStore(messageId).Content(attachmentId).Get(),
    // возвращают Task<byte[]> — общий формат, разные эндпоинты). Отдельный
    // метод, а не переиспользование UploadCallRecordingAsync целиком: тот
    // читает recordingId сам по себе (/recording/{id}/content), у голосовой
    // почты аудио лежит под attachment id внутри конкретного сообщения
    // расширения (/extension/{extId}/message-store/{messageId}/content/{attachmentId}).
    public async Task<RecordingUploadResult> UploadVoicemailRecordingAsync(string extensionId, string messageId, string attachmentId)
    {
        try
        {
            _logger.LogInformation("Downloading voicemail attachment {AttachmentId} (message {MessageId}) from RingCentral...", attachmentId, messageId);

            byte[] recordingContent;
            try
            {
                recordingContent = await RunWithRcRetryAsync(
                    () => _rc.Restapi().Account().Extension(extensionId).MessageStore(messageId).Content(attachmentId).Get(),
                    $"VoicemailContent {messageId}/{attachmentId}");
            }
            catch (RestException ex)
            {
                _logger.LogError(ex, "Voicemail {MessageId}: attachment download failed after retries", messageId);
                return RecordingUploadResult.Failed();
            }

            if (recordingContent == null || recordingContent.Length == 0)
            {
                _logger.LogWarning("Voicemail {MessageId} attachment is empty or not available", messageId);
                return RecordingUploadResult.NotAvailable();
            }

            return await UploadBytesToAmoDriveAsync(recordingContent, $"voicemail_{messageId}.mp3", "audio/mpeg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading voicemail attachment {AttachmentId} (message {MessageId}) to amoCRM drive", attachmentId, messageId);
            return RecordingUploadResult.Failed();
        }
    }

    // Скачивает текст расшифровки голосового (вложение AudioTranscription —
    // по данным RC, plain text). Возвращает null при любой проблеме
    // (скачивание не удалось, пустой контент, невалидная кодировка) —
    // вызывающий код трактует это как "расшифровки нет", ровно то же, что и
    // vmTranscriptionStatus != Completed (см. задачу: код обязан работать
    // и без текста).
    public async Task<string> FetchVoicemailTranscriptAsync(string extensionId, string messageId, string attachmentId)
    {
        try
        {
            var bytes = await RunWithRcRetryAsync(
                () => _rc.Restapi().Account().Extension(extensionId).MessageStore(messageId).Content(attachmentId).Get(),
                $"VoicemailTranscript {messageId}/{attachmentId}");

            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            return Encoding.UTF8.GetString(bytes).Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch voicemail transcript for message {MessageId}, attachment {AttachmentId}", messageId, attachmentId);
            return null;
        }
    }

    // Возвращает true только при 2xx-ответе amoCRM на создание заметки.
    // Вызывающий код (ProcessSingleCallAsync) обязан проверить результат —
    // не-2xx не должен трактоваться как успех.
    public async Task<bool> CreateCallNoteAsync(long leadId, RingCentral.CallLogRecord record, string recURL, string customerPhoneNumber, bool isMissed, DateTime? callStartUtc = null)
    {
        const string entityType = "leads";
        const long responsibleUserId = 8644141;

        // amoCRM params для call_in/call_out ограничены документированным набором
        // полей (uniq, duration, source, link, phone, call_responsible) — отдельного
        // поля под статус звонка нет, поэтому пометка о пропущенном звонке идёт в source.
        // amoCRM требует params.source непустым (400 NotBlank/NotNullable) — RC не
        // гарантирует name в CallLog для внешних абонентов без записи в адресной
        // книге (это может быть null/пусто даже для валидного, обработанного
        // звонка), так что запасное значение обязательно.
        var source = string.IsNullOrEmpty(record.from.extensionId) ? record.from.name : record.to.name;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = "RingCentral";
        }
        if (isMissed)
        {
            source = $"{source} (пропущенный звонок)";
        }

        // Человекочитаемая метка времени звонка в тексте — страховка на случай,
        // если amoCRM скорректирует created_at для очень старых дат (типично
        // для звонков, найденных поздней привязкой, возрастом до LookbackDays).
        if (callStartUtc.HasValue)
        {
            source = $"{source} [звонок {callStartUtc.Value:dd.MM.yyyy HH:mm} UTC]";
        }

        // Создаем объект params
        var paramsObject = new JsonObject
        {
            ["uniq"] = $"{record.id}",
            ["duration"] = record.duration,
            ["source"] = source,
            ["phone"] = $"{customerPhoneNumber}",
            ["call_responsible"] = $"{record.to.phoneNumber} - {record.to.name}"
        };

        // Добавляем link только если он не пустой
        if (!string.IsNullOrEmpty(recURL))
        {
            paramsObject["link"] = recURL;
        }

        var noteObject = new JsonObject
        {
            ["note_type"] = record.direction == "Inbound" ? "call_in" : "call_out",
            ["params"] = paramsObject
        };

        // ВНИМАНИЕ: created_at как принимаемое поле при создании заметки НЕ
        // подтверждён официальной документацией amoCRM v4 (документированный
        // список полей для POST .../notes — entity_id/note_type/params/
        // created_by/responsible_user_id/request_id/is_need_to_trigger_digital_pipeline,
        // created_at в ответах описан как read-only). Отправляем его как
        // попытку — по опыту многие клиенты amoCRM его всё же принимают, а
        // human-readable время в source (выше) служит страховкой на случай,
        // если поле проигнорируется. Требует эмпирической проверки на
        // реальном аккаунте.
        if (callStartUtc.HasValue)
        {
            noteObject["created_at"] = ((DateTimeOffset)DateTime.SpecifyKind(callStartUtc.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();
        }

        var rootJsonArray = new JsonArray { noteObject };
        
        var content = new StringContent(rootJsonArray.ToJsonString(), Encoding.UTF8, "application/json");
        var url = $"/api/v4/{entityType}/{leadId}/notes";
        var resp = await _httpClient.PostAsync(url, content);

        if (resp.IsSuccessStatusCode)
        {
            _logger.LogInformation("✅ Note added to amoCRM (Lead ID: {LeadId}) successfully. Recording link: {HasLink}",
                leadId, !string.IsNullOrEmpty(recURL) ? "included" : "not available");
            return true;
        }

        var err = await resp.Content.ReadAsStringAsync();
        _logger.LogError("Failed to add note: {StatusCode} {ErrorBody}", resp.StatusCode, err);
        return false;
    }

    // Часовой пояс для отображения времени голосового в call_responsible
    // (по явному запросу — только для голосовых, не для звонков, см.
    // CreateCallNoteAsync). Ленивая инициализация через Lazy<T>, а не
    // статический readonly-конструктор напрямую: TimeZoneNotFoundException/
    // InvalidTimeZoneException (например, если у хоста нет данных ICU) не
    // должна ронять загрузку всего класса AmoCrmService при старте процесса —
    // упасть должен только вызов, реально нуждающийся в этом поясе, и только
    // при первом обращении.
    private static readonly Lazy<TimeZoneInfo> EasternTimeZoneLazy = new(() =>
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    private static TimeZoneInfo EasternTimeZone => EasternTimeZoneLazy.Value;

    // Создаёт заметку по голосовому сообщению. note_type="call_in" (решение
    // принято явно, не common): только call_in/call_out принимают
    // params.uniq и params.link в amoCRM v4 (common — только text), так что
    // это единственный тип с рабочей дедупликацией через NoteExistsAsync и
    // полем под ссылку на запись. Компромисс: голосовые попадут в статистику
    // звонков amoCRM — текст заметки поэтому явно начинается с пометки, что
    // это голосовое, а не разговор.
    //
    // transcript — null/пусто, если vmTranscriptionStatus != Completed или
    // вложения AudioTranscription не было (см. задачу: часть голосовых на
    // проде без текста, код обязан работать и без него).
    public async Task<bool> CreateVoicemailNoteAsync(
        long leadId,
        string voicemailId,
        string callerNumber,
        string callerName,
        DateTime messageTimeUtc,
        string transcript,
        string recordingUrl,
        long durationSeconds)
    {
        const string entityType = "leads";

        var callerLabel = string.IsNullOrWhiteSpace(callerName) ? callerNumber : callerName;
        if (string.IsNullOrWhiteSpace(callerLabel))
        {
            // Тот же обязательный fallback, что и у звонков (CLAUDE.md,
            // params.source не может быть пустым — 400 NotBlank/NotNullable).
            // Скрытый номер (callerNumber тоже пуст) отсекается раньше, в
            // вызывающем коде (action=no_number) — до создания заметки этот
            // метод не доходит, но fallback оставлен как последний рубеж.
            callerLabel = "RingCentral";
        }

        // call_in принимает только документированный набор полей params
        // (uniq/duration/source/link/phone/call_responsible) — отдельного
        // текстового поля нет (в отличие от common/sms_in/sms_out, у которых
        // есть params.text, но они не принимают uniq/link, см. выбор типа
        // заметки выше). source — короткая пометка с именем/номером и явным
        // указанием, что это голосовое (иначе неотличимо от звонка в списке
        // заметок); расшифровка и время сообщения идут в call_responsible —
        // единственное свободное строковое поле схемы.
        var source = $"{callerLabel} (голосовое сообщение)";
        var transcriptText = string.IsNullOrWhiteSpace(transcript) ? "расшифровка недоступна" : transcript;

        // Время для отображения — локальная зона сервера (America/New_York),
        // а не UTC: только для этой (voicemail) заметки, по явному запросу —
        // CreateCallNoteAsync для обычных звонков продолжает печатать UTC как
        // раньше (звонки этот запрос не затрагивает).
        // "America/New_York" — IANA id, начиная с .NET 6 TimeZoneInfo
        // резолвит их на всех платформах (в т.ч. Windows), Windows-специфичный
        // id вроде "Eastern Standard Time" не нужен. Fallback на UTC при
        // сбое поиска пояса (например, нет данных ICU на хосте) — заметка
        // всё равно должна создаться, просто с UTC-временем вместо
        // локального, а не провалиться целиком из-за косметики.
        DateTime displayTime;
        try
        {
            displayTime = TimeZoneInfo.ConvertTimeFromUtc(messageTimeUtc, EasternTimeZone);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert voicemail time to America/New_York, falling back to UTC");
            displayTime = messageTimeUtc;
        }

        // note_type=call_in подписывает заметку в интерфейсе amoCRM как
        // "Входящий звонок" — без явной пометки голосовое неотличимо от
        // обычного звонка в списке заметок сделки.
        var callResponsible = $"Голосовое сообщение [{displayTime:dd.MM.yyyy HH:mm}] {transcriptText}";

        // Прод: 400 FieldMissing на params.duration — не задокументировано
        // заранее amoCRM (как и params.source, см. CLAUDE.md), обязательность
        // выяснена только по факту отказа. У голосовых нет отдельного понятия
        // длительности разговора — используем vmDuration вложения
        // AudioRecording (вызывающий код передаёт 0, если вложения/поля нет,
        // лишь бы поле в params присутствовало).
        var paramsObject = new JsonObject
        {
            ["uniq"] = voicemailId,
            ["duration"] = durationSeconds,
            ["source"] = source,
            ["phone"] = $"{callerNumber}",
            ["call_responsible"] = callResponsible
        };

        if (!string.IsNullOrEmpty(recordingUrl))
        {
            paramsObject["link"] = recordingUrl;
        }

        var noteObject = new JsonObject
        {
            ["note_type"] = "call_in",
            ["params"] = paramsObject,

            // Тот же непроверенный официально, но эмпирически рабочий приём,
            // что и у CreateCallNoteAsync (см. комментарий там) — created_at
            // как время самого сообщения, а не момент обработки вебхука.
            ["created_at"] = ((DateTimeOffset)DateTime.SpecifyKind(messageTimeUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()
        };

        var rootJsonArray = new JsonArray { noteObject };

        var content = new StringContent(rootJsonArray.ToJsonString(), Encoding.UTF8, "application/json");
        var url = $"/api/v4/{entityType}/{leadId}/notes";
        var resp = await _httpClient.PostAsync(url, content);

        if (resp.IsSuccessStatusCode)
        {
            _logger.LogInformation("✅ Voicemail note added to amoCRM (Lead ID: {LeadId}). RingCentral message id: {VoicemailId}, recording: {HasLink}",
                leadId, voicemailId, !string.IsNullOrEmpty(recordingUrl) ? "included" : "not available");
            return true;
        }

        var voicemailErr = await resp.Content.ReadAsStringAsync();
        _logger.LogError("Failed to add voicemail note: {StatusCode} {ErrorBody}. RingCentral message id: {VoicemailId}", resp.StatusCode, voicemailErr, voicemailId);
        return false;
    }

    // Единая точка обработки одного звонка. Вызывается и основным поллингом
    // (record только что увиден, свежий), и поздней привязкой (record может
    // быть до LateAttach:LookbackDays суток "старым"). logPrefix уходит в
    // каждую лог-строку, чтобы различать источник в общем логе.
    //
    // failClosed передаётся в NoteExistsAsync как есть: основной поллинг
    // (свежий звонок, узкое окно, следующего шанса нет) — fail-open по
    // умолчанию; поздняя привязка и догон при старте вызывают с true — у
    // них есть следующий цикл опроса, так что при невозможности достоверно
    // проверить наличие заметки безопаснее вернуть Error и не помечать
    // звонок обработанным, чем рискнуть создать дубль.
    public async Task<CallProcessingResult> ProcessSingleCallAsync(
        RingCentral.CallLogRecord record,
        CallProcessingGuard guard,
        string logPrefix,
        DateTime? callStartUtc = null,
        bool failClosed = false)
    {
        if (record.from == null || record.to == null)
        {
            _logger.LogWarning("{Prefix} id={Id} action=error reason=missing_from_or_to", logPrefix, record.id);
            return CallProcessingResult.Error;
        }

        var searchNumber = record.from.extensionId == null
            ? record.from.phoneNumber
            : record.to.phoneNumber;

        if (string.IsNullOrWhiteSpace(searchNumber))
        {
            _logger.LogWarning("{Prefix} id={Id} action=error reason=no_number", logPrefix, record.id);
            return CallProcessingResult.NoNumber;
        }

        using (await guard.AcquireAsync(record.id))
        {
            if (guard.IsProcessed(record.id))
            {
                _logger.LogInformation("{Prefix} id={Id} number={Number} action=dup", logPrefix, record.id, searchNumber);
                return CallProcessingResult.Duplicate;
            }

            var candidateLeads = await FindLeadByPhoneNumberAsync(searchNumber);
            if (candidateLeads == null)
            {
                _logger.LogInformation("{Prefix} id={Id} number={Number} lead=none action=no_lead", logPrefix, record.id, searchNumber);
                return CallProcessingResult.NoLead;
            }

            var targetLeadId = await ResolveTargetLeadAsync(candidateLeads);
            if (targetLeadId == null)
            {
                _logger.LogInformation("{Prefix} id={Id} number={Number} lead=none action=no_lead", logPrefix, record.id, searchNumber);
                return CallProcessingResult.NoLead;
            }

            var noteType = record.direction == "Inbound" ? "call_in" : "call_out";

            bool noteExists;
            try
            {
                noteExists = await NoteExistsAsync(targetLeadId.Value, noteType, record.id, failClosed, callStartUtc);
            }
            catch (NoteExistenceUnknownException ex)
            {
                // Не помечаем guard.MarkProcessed — звонок остаётся доступным
                // для повторной попытки на следующем цикле/проходе.
                _logger.LogWarning(ex, "{Prefix} id={Id} number={Number} lead={LeadId} action=error reason=note_check_failed",
                    logPrefix, record.id, searchNumber, targetLeadId);
                return CallProcessingResult.Error;
            }

            if (noteExists)
            {
                guard.MarkProcessed(record.id);
                _logger.LogInformation("{Prefix} id={Id} number={Number} lead={LeadId} action=dup", logPrefix, record.id, searchNumber, targetLeadId);
                return CallProcessingResult.Duplicate;
            }

            string permanentRecordingUrl = null;
            if (record.recording?.id != null)
            {
                var uploadResult = await UploadCallRecordingAsync(record.recording.id, record.id);

                if (uploadResult.Outcome == RecordingUploadOutcome.Uploaded)
                {
                    permanentRecordingUrl = uploadResult.Url;
                }
                else if (uploadResult.Outcome == RecordingUploadOutcome.Failed)
                {
                    // failClosed (LATE/STARTUP) — есть следующий цикл/деплой: откладываем
                    // звонок целиком, чтобы не потерять запись навсегда (заметка без
                    // записи создаётся один раз и запись к ней потом уже не добавить).
                    // Не failClosed (POLL, узкое 5-минутное окно, следующего шанса нет) —
                    // как и раньше, создаём заметку без записи, чтобы не потерять звонок.
                    if (failClosed)
                    {
                        _logger.LogWarning(
                            "{Prefix} id={Id} number={Number} lead={LeadId} action=error reason=recording_download_failed",
                            logPrefix, record.id, searchNumber, targetLeadId);
                        return CallProcessingResult.Error;
                    }

                    _logger.LogWarning(
                        "{Prefix} id={Id} number={Number} lead={LeadId} reason=recording_download_failed: creating note without recording (no next attempt in this path)",
                        logPrefix, record.id, searchNumber, targetLeadId);
                }
                // RecordingUploadOutcome.NotAvailable — у звонка на стороне RC нет
                // записи, ждать нечего, создаём заметку без ссылки как обычно.
            }

            bool isMissed = record.result != null && MissedCallResults.Contains(record.result);

            bool noteCreated = await CreateCallNoteAsync(targetLeadId.Value, record, permanentRecordingUrl, searchNumber, isMissed, callStartUtc);
            if (!noteCreated)
            {
                // Не помечаем guard.MarkProcessed — заметка не создана, звонок
                // остаётся доступным для повторной попытки на следующем
                // опросе/цикле/проходе. Дубль здесь невозможен: NoteExistsAsync
                // на следующей попытке не найдёт заметку, т.к. её не существует.
                _logger.LogWarning("{Prefix} id={Id} number={Number} lead={LeadId} action=error reason=note_create_failed",
                    logPrefix, record.id, searchNumber, targetLeadId);
                return CallProcessingResult.Error;
            }

            guard.MarkProcessed(record.id);

            _logger.LogInformation("{Prefix} id={Id} number={Number} lead={LeadId} action=attached", logPrefix, record.id, searchNumber, targetLeadId);
            return CallProcessingResult.Attached;
        }
    }
}