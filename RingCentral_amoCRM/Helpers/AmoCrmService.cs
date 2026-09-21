using Newtonsoft.Json;
using RingCentral;
using RingCentral_amoCRM.Helpers;
using RingCentral_amoCRM.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static System.Net.WebRequestMethods;
using JsonSerializer = System.Text.Json.JsonSerializer;

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
        IEnumerable<long> leadsids;
        if (contact.Embedded.Contacts.Any(c => c.Embedded.Leads.Any()))
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

    // Create a note attached to an entity (lead/contact). element_type: 1 = contact, 2 = lead
    public async Task CreateNoteAsync(long leadId, string noteText, string phone)
    {
        const string entityType = "leads"; 
    
        const long responsibleUserId = 8644141;

        var rootJsonArray = new JsonArray
        {
            new JsonObject
            {
                ["note_type"] = "sms_in", 
                ["params"] = new JsonObject 
                { 
                    ["text"] = $"Входящее SMS от {phone}: {noteText}",
                    ["phone"] = $"{phone}"
                }
            }
        };

        var content = new StringContent(rootJsonArray.ToJsonString(), Encoding.UTF8, "application/json");
        var url = $"/api/v4/{entityType}/{leadId}/notes";
        var resp = await _httpClient.PostAsync(url, content);
        if (resp.IsSuccessStatusCode)
        {
            _logger.LogInformation("Note added to amoCRM (Lead ID: {LeadId}) successfully.", leadId);
        }
        else
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Failed to add note: {StatusCode} {ErrorBody}", resp.StatusCode, err);
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

    public async Task CreateCallNoteAsync(long leadId, RingCentral.CallLogRecord record, string recURL)
    {
        const string entityType = "leads";
        const long responsibleUserId = 8644141;

        // Создаем объект params
        var paramsObject = new JsonObject
        {
            ["uniq"] = $"{record.id}",
            ["duration"] = record.duration,
            ["source"] = $"{(string.IsNullOrEmpty(record.from.extensionId) ? record.from.name : record.to.name)}",
            ["phone"] = $"{record.from.phoneNumber}",
            ["call_responsible"] = $"{record.to.phoneNumber} - {record.to.name}"
        };

        // Добавляем link только если он не пустой
        if (!string.IsNullOrEmpty(recURL))
        {
            paramsObject["link"] = recURL;
        }

        var rootJsonArray = new JsonArray
        {
            new JsonObject
            {
                ["note_type"] = record.direction == "Inbound" ? "call_in" : "call_out", 
                ["params"] = paramsObject
            }
        };
        
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
}