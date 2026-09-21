using Newtonsoft.Json;
using RingCentral;
using RingCentral_amoCRM.Helpers;
using RingCentral_amoCRM.Models;
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

    public AmoCrmService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<AmoCrmService> logger, RestClient rc)
    {
        _configuration = configuration;
        _logger = logger;
        _rc = rc;

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
            _logger.LogError($"No leads found by number {phoneNumber}");
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

    // Брошено NoteExistsAsync(failClosed: true), когда мы не смогли достоверно
    // проверить, есть ли уже заметка (сбой запроса, не-успешный статус после
    // повторов, или исчерпан лимит страниц). Вызывающий код должен трактовать
    // это как ошибку обработки звонка, а не как "заметки нет".
    public class NoteExistenceUnknownException : Exception
    {
        public NoteExistenceUnknownException(string message, Exception inner = null)
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
    // CreateCallNoteAsync). Это чисто серверная оптимизация поверх основного
    // скана по note_type, а не замена ему: если фильтр не даст ожидаемого
    // эффекta, логика останется корректной, просто медленнее. Требует
    // эмпирической проверки на реальном amoCRM-аккаунте.
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
        if (notEarlierThanUtc.HasValue)
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
    public async Task CreateNoteAsync(long leadId, string noteText, string phone, string noteType, string uniqId)
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
        }
        else
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Failed to add note: {StatusCode} {ErrorBody}. RingCentral message id: {MessageId}", resp.StatusCode, err, uniqId);
        }
    }

    public async Task<string> UploadCallRecordingAsync(string recordingId, string callId)
    {
        try
        {
            _logger.LogInformation("Downloading recording {RecordingId} from RingCentral...", recordingId);

            // Скачиваем запись из RingCentral
            var recordingContent = await _rc.Restapi().Account().Recording(recordingId).Content().Get();
            
            if (recordingContent == null || recordingContent.Length == 0)
            {
                _logger.LogWarning("Recording {RecordingId} is empty or not available", recordingId);
                return null;
            }

            _logger.LogInformation("Recording downloaded, size: {Size} bytes. Uploading to amoCRM drive...", recordingContent.Length);

            // Получаем drive_url для текущего аккаунта
            var accountResponse = await _httpClient.GetAsync("/api/v4/account?with=drive_url");
            if (!accountResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get account drive_url: {StatusCode}", accountResponse.StatusCode);
                return null;
            }

            var accountJson = await accountResponse.Content.ReadAsStringAsync();
            var accountData = JsonSerializer.Deserialize<JsonElement>(accountJson);
            
            string driveUrl = accountData.GetProperty("drive_url").GetString();
            if (string.IsNullOrEmpty(driveUrl))
            {
                _logger.LogError("Drive URL is empty in account response");
                return null;
            }

            _logger.LogInformation("Got drive URL: {DriveUrl}", driveUrl);

            // Шаг 1: Создаем сессию загрузки
            var sessionPayload = new
            {
                file_name = $"call_recording_{callId}.mp3",
                file_size = recordingContent.Length,
                content_type = "audio/mpeg"
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
                return null;
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
            
            while (offset < recordingContent.Length)
            {
                int partSize = Math.Min(maxPartSize, recordingContent.Length - offset);
                var partContent = new ByteArrayContent(recordingContent, offset, partSize);
                partContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");

                _logger.LogInformation("Uploading part: offset={Offset}, size={PartSize}", offset, partSize);

                var uploadResponse = await driveClient.PostAsync(nextUrl, partContent);
                
                if (!uploadResponse.IsSuccessStatusCode)
                {
                    var error = await uploadResponse.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to upload file part: {StatusCode} {Error}", uploadResponse.StatusCode, error);
                    return null;
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
                        return downloadUrl;
                    }
                    
                    _logger.LogInformation("✅ File uploaded successfully. UUID: {FileUuid}", fileUuid);
                    break;
                }
            }

            if (string.IsNullOrEmpty(fileUuid))
            {
                _logger.LogError("Failed to get file UUID after upload");
                return null;
            }

            // Если не получили download URL из ответа, формируем его вручную
            var finalDownloadUrl = $"{driveUrl}/download/{fileUuid}";
            _logger.LogInformation("✅ Recording uploaded to amoCRM drive: {Url}", finalDownloadUrl);
            
            return finalDownloadUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading recording {RecordingId} to amoCRM drive", recordingId);
            return null;
        }
    }

    public async Task CreateCallNoteAsync(long leadId, RingCentral.CallLogRecord record, string recURL, string customerPhoneNumber, bool isMissed, DateTime? callStartUtc = null)
    {
        const string entityType = "leads";
        const long responsibleUserId = 8644141;

        // amoCRM params для call_in/call_out ограничены документированным набором
        // полей (uniq, duration, source, link, phone, call_responsible) — отдельного
        // поля под статус звонка нет, поэтому пометка о пропущенном звонке идёт в source.
        var source = string.IsNullOrEmpty(record.from.extensionId) ? record.from.name : record.to.name;
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
        }
        else
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Failed to add note: {StatusCode} {ErrorBody}", resp.StatusCode, err);
        }
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
                permanentRecordingUrl = await UploadCallRecordingAsync(record.recording.id, record.id);
            }

            bool isMissed = record.result != null && MissedCallResults.Contains(record.result);

            await CreateCallNoteAsync(targetLeadId.Value, record, permanentRecordingUrl, searchNumber, isMissed, callStartUtc);
            guard.MarkProcessed(record.id);

            _logger.LogInformation("{Prefix} id={Id} number={Number} lead={LeadId} action=attached", logPrefix, record.id, searchNumber, targetLeadId);
            return CallProcessingResult.Attached;
        }
    }
}