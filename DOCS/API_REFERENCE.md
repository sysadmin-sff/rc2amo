# API Reference Documentation

## Overview

This document provides detailed API reference for all controllers, services, and methods in the RingCentral to amoCRM Integration application.

---

## Table of Contents

1. [Controllers](#controllers)
   - [RingCentralWebHookController](#ringcentralwebhookcontroller)
   - [AmoCrmController](#amocrmcontroller)
2. [Services](#services)
   - [AmoCrmService](#amocrmservice)
   - [SubscriptionService](#subscriptionservice)
   - [CallLogPollingService](#calllogpollingservice)
   - [SubscriptionHostedService](#subscriptionhostedservice)
3. [Models](#models)

---

## Controllers

### RingCentralWebHookController

**Namespace**: `RingCentral_amoCRM.Controllers`

**Route**: `/api/RingCentralWebHook`

**Purpose**: Handles incoming WebHook notifications from RingCentral for SMS and call events.

#### Constructor

```csharp
public RingCentralWebHookController(
    ILogger<RingCentralWebHookController> logger,
    RestClient restClient,
    IConfiguration configuration,
    AmoCrmService amoService)
```

**Parameters**:
- `logger`: Logger instance for diagnostic output
- `restClient`: RingCentral API client
- `configuration`: Application configuration (reads JWT token)
- `amoService`: Service for amoCRM operations

#### Methods

##### `HandleWebHookOrValidation()`

```csharp
[HttpPost]
[HttpGet]
[Route("webhook")]
public async Task<IActionResult> HandleWebHookOrValidation()
```

**Purpose**: Main WebHook endpoint for SMS notifications. Handles both validation handshake and actual SMS events.

**Process Flow**:
1. Check for `Validation-Token` header (first-time setup)
2. If present, echo token back and return 200 OK
3. If not, read request body as JSON
4. Deserialize to `RingCentralNotification`
5. Extract SMS details (sender, receiver, text)
6. Find leads in amoCRM by phone number
7. Create note for each matching lead

**Returns**:
- `200 OK`: Validation successful or no action needed
- `201 Created`: Note successfully added to amoCRM

**Example Request (Validation)**:
```http
GET /api/RingCentralWebHook/webhook
Validation-Token: abc123xyz
```

**Example Response (Validation)**:
```http
HTTP/1.1 200 OK
Validation-Token: abc123xyz
```

**Example Request (SMS Event)**:
```json
{
  "uuid": "6099091745625140455",
  "event": "/restapi/v1.0/account/335731037/extension/3100110036/message-store/instant?type=SMS",
  "timestamp": "2025-01-01T20:48:17.505Z",
  "subscriptionId": "ee938ed7-cfde-4d67-89f9-6f58c10bfd47",
  "body": {
    "id": "3158995804037",
    "from": {
      "phoneNumber": "+79788172077",
      "name": "John Doe"
    },
    "to": [{
      "phoneNumber": "+12397442122",
      "name": "Muhammad SFF"
    }],
    "type": "SMS",
    "direction": "Inbound",
    "subject": "Hello, this is a test message"
  }
}
```

##### `HandleCallWebHook()`

```csharp
[HttpPost]
[Route("webhook/call")]
public async Task<IActionResult> HandleCallWebHook()
```

**Purpose**: Alternative WebHook endpoint for call events (telephony sessions).

**Note**: This endpoint is currently optional. The main call processing happens via polling in `CallLogPollingService`.

**Process**:
1. Validate token if present
2. Parse JSON body defensively (uses JsonDocument)
3. Extract caller phone number from various possible structures
4. Find leads and create notes

**Returns**: `200 OK` always (to prevent RingCentral retries)

##### `GetSubscriptions()`

```csharp
[HttpGet]
[Route("Subscriptions")]
public async Task<IActionResult> GetSubscriptions()
```

**Purpose**: Lists all active RingCentral WebHook subscriptions.

**Returns**:
- `200 OK`: Array of subscription objects
- `404 Not Found`: No active subscriptions
- `500 Internal Server Error`: API call failed

**Example Response**:
```json
[
  {
    "id": "ee938ed7-cfde-4d67-89f9-6f58c10bfd47",
    "uri": "https://platform.ringcentral.com/restapi/v1.0/subscription/ee938ed7-cfde-4d67-89f9-6f58c10bfd47",
    "eventFilters": [
      "/restapi/v1.0/account/~/extension/3100110036/message-store/instant?type=SMS"
    ],
    "expirationTime": "2025-01-06T20:48:17.505Z",
    "deliveryMode": {
      "transportType": "WebHook",
      "address": "https://your-domain.com/api/RingCentralWebHook/webhook"
    }
  }
]
```

##### `DeleteSub()`

```csharp
[HttpPost]
[Route("Delete subscription")]
public async Task<IActionResult> DeleteSub(String subscriptionId)
```

**Purpose**: Deletes a specific RingCentral subscription by ID.

**Parameters**:
- `subscriptionId` (string): The subscription ID to delete

**Returns**:
- `200 OK`: Deleted successfully
- `500 Internal Server Error`: Deletion failed

**Example Usage**:
```http
POST /api/RingCentralWebHook/Delete%20subscription?subscriptionId=ee938ed7-cfde-4d67-89f9-6f58c10bfd47
```

##### `GetMessages()`

```csharp
[HttpGet]
[Route("Messages")]
public async Task<IActionResult> GetMessages()
```

**Purpose**: Debugging endpoint to retrieve messages for a specific extension.

**Hardcoded Extension**: `3017722036` (change as needed)

**Returns**: `200 OK` with message list

##### `GetUsers()`

```csharp
[HttpGet]
[Route("Users")]
public async Task<IActionResult> GetUsers()
```

**Purpose**: Debugging endpoint to list all RingCentral extensions.

**Returns**: `200 OK` with extension list

---

### AmoCrmController

**Namespace**: `RingCentral_amoCRM.Controllers`

**Route**: `/oauth`

**Purpose**: Handles OAuth2 authorization flow for amoCRM integration.

#### Constructor

```csharp
public AmoCrmController(
    AmoCrmService amoCrmService,
    ILogger<AmoCrmController> logger)
```

#### Methods

##### `Callback()`

```csharp
[HttpGet("callback")]
public async Task<IActionResult> Callback([FromQuery] string code)
```

**Purpose**: OAuth2 callback endpoint. Called by amoCRM after user authorizes the integration.

**Process**:
1. Extract authorization code from query string
2. Exchange code for access/refresh tokens
3. Save tokens to configuration file
4. Return success message

**Parameters**:
- `code` (string, required): Authorization code from amoCRM

**Returns**:
- `200 OK`: "? Интеграция amoCRM успешно авторизована и токены сохранены!"
- `400 Bad Request`: Missing or invalid code
- `500 Internal Server Error`: Token exchange failed

**Authorization URL** (for initial setup):
```
https://{subdomain}.amocrm.ru/oauth?client_id={ClientId}&redirect_uri={RedirectUri}&response_type=code
```

**Example Callback**:
```http
GET /oauth/callback?code=def50200a1b2c3d4e5f6...
```

---

## Services

### AmoCrmService

**Namespace**: `RingCentral_amoCRM.Helpers`

**Lifetime**: Singleton

**Purpose**: Core business logic for interacting with amoCRM API, including authentication, lead search, and note creation.

#### Constructor

```csharp
public AmoCrmService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<AmoCrmService> logger,
    RestClient rc)
```

**Dependencies**:
- `IHttpClientFactory`: Creates configured HTTP client for amoCRM API
- `IConfiguration`: Reads credentials from appsettings.json
- `ILogger`: Logging
- `RestClient`: RingCentral API client (for call recording downloads)

#### Configuration Properties

Reads from `appsettings.json`:
- `AmoCrm:ClientId`
- `AmoCrm:ClientSecret`
- `AmoCrm:RedirectUri`
- `AmoCrm:Subdomain`
- `AmoCrm:RefreshToken`
- `AmoCrm:AccessToken`

#### Methods

##### `InitializeAsync()`

```csharp
public async Task InitializeAsync()
```

**Purpose**: Initializes the service by refreshing access token using stored refresh token.

**Called**: 
- On application startup (in `Program.cs`)
- Before each background service iteration

**Process**:
1. Read `RefreshToken` from configuration
2. If found, call `RefreshTokensAsync()`
3. If not found, log warning

**Returns**: `Task` (void async)

##### `GetAccessTokenAsync()`

```csharp
public async Task<string> GetAccessTokenAsync()
```

**Purpose**: Returns a valid access token, refreshing if expired.

**Process**:
1. Check if `_currentToken` is null or expired
2. If yes, refresh using stored refresh token
3. Return current access token

**Returns**: `string` - Valid access token

**Throws**: `InvalidOperationException` if no refresh token available

##### `RefreshTokensAsync()`

```csharp
private async Task RefreshTokensAsync(string refreshToken)
```

**Purpose**: Exchanges refresh token for new access/refresh tokens.

**Parameters**:
- `refreshToken` (string): Current refresh token

**Process**:
1. Build OAuth2 token request payload
2. POST to `/oauth2/access_token`
3. Deserialize response to `AmoCrmToken`
4. Update `_currentToken`
5. Update HTTP client authorization header
6. Persist new tokens to `appsettings.json`

**Retry Logic**:
- Max retries: 3
- Initial delay: 1000ms
- Exponential backoff (doubles each retry)

**Error Handling**:
- Logs all HTTP errors with inner exception details
- Returns silently on failure (token remains unchanged)

**OAuth2 Request**:
```http
POST /oauth2/access_token
Content-Type: application/x-www-form-urlencoded

client_id={ClientId}&
client_secret={ClientSecret}&
grant_type=refresh_token&
refresh_token={RefreshToken}&
redirect_uri={RedirectUri}
```

##### `ExchangeCodeForTokensAsync()`

```csharp
public async Task<string> ExchangeCodeForTokensAsync(string code)
```

**Purpose**: Initial token exchange using authorization code (first-time setup).

**Parameters**:
- `code` (string): Authorization code from OAuth2 callback

**Process**:
1. Build OAuth2 token request with authorization code
2. POST to `/oauth2/access_token`
3. Deserialize tokens
4. Save to configuration
5. Return refresh token

**Returns**: `string` - Refresh token (or empty string on failure)

**OAuth2 Request**:
```http
POST /oauth2/access_token
Content-Type: application/x-www-form-urlencoded

client_id={ClientId}&
client_secret={ClientSecret}&
grant_type=authorization_code&
code={AuthorizationCode}&
redirect_uri={RedirectUri}
```

##### `FindLeadByPhoneNumberAsync()`

```csharp
public async Task<IEnumerable<long>> FindLeadByPhoneNumberAsync(string phoneNumber)
```

**Purpose**: Searches for amoCRM leads associated with a phone number.

**Parameters**:
- `phoneNumber` (string): Phone number to search (e.g., "+1234567890")

**Process**:
1. Clean phone number (remove non-digit characters)
2. Remove leading digit (e.g., "1234567890" from "+11234567890")
3. GET `/api/v4/contacts?query={cleanNumber}&with=leads`
4. Deserialize response
5. Extract lead IDs from embedded leads
6. Return list of lead IDs

**Returns**: `IEnumerable<long>` - Lead IDs, or `null` if not found

**Example Response from amoCRM**:
```json
{
  "_embedded": {
    "contacts": [
      {
        "id": 12345,
        "name": "John Doe",
        "_embedded": {
          "leads": [
            { "id": 67890 },
            { "id": 67891 }
          ]
        }
      }
    ]
  }
}
```

##### `CreateNoteAsync()`

```csharp
public async Task CreateNoteAsync(long leadId, string noteText, string phone)
```

**Purpose**: Creates an SMS note in an amoCRM lead.

**Parameters**:
- `leadId` (long): Lead ID
- `noteText` (string): Note content
- `phone` (string): Phone number

**Note Type**: `sms_in` (Incoming SMS)

**Process**:
1. Build JSON array with note object
2. POST to `/api/v4/leads/{leadId}/notes`
3. Log success or error

**Request Body**:
```json
[
  {
    "note_type": "sms_in",
    "params": {
      "text": "Входящее SMS от +1234567890: Hello!",
      "phone": "+1234567890"
    }
  }
]
```

**Returns**: `Task` (void async)

##### `CreateCallNoteAsync()`

```csharp
public async Task CreateCallNoteAsync(
    long leadId,
    RingCentral.CallLogRecord record,
    string recURL)
```

**Purpose**: Creates a call note in an amoCRM lead, optionally including recording link.

**Parameters**:
- `leadId` (long): Lead ID
- `record` (CallLogRecord): RingCentral call log record
- `recURL` (string, optional): Recording download URL

**Note Type**: 
- `call_in` (Inbound call)
- `call_out` (Outbound call)

**Process**:
1. Build JSON with call details
2. Add recording link if available
3. POST to `/api/v4/leads/{leadId}/notes`
4. Log result

**Request Body**:
```json
[
  {
    "note_type": "call_in",
    "params": {
      "uniq": "AJnTczHyq8nAts1A",
      "duration": 52,
      "source": "Muhammad SFF",
      "phone": "+12397442122",
      "call_responsible": "+13152634256 - SNIDER GLENDON",
      "link": "https://drive.amocrm.ru/download/uuid-here"
    }
  }
]
```

**Returns**: `Task` (void async)

##### `UploadCallRecordingAsync()`

```csharp
public async Task<string> UploadCallRecordingAsync(
    string recordingId,
    string callId)
```

**Purpose**: Downloads call recording from RingCentral and uploads to amoCRM Drive.

**Parameters**:
- `recordingId` (string): RingCentral recording ID
- `callId` (string): Call ID (for filename)

**Process**:
1. Download recording via `_rc.Restapi().Account().Recording(recordingId).Content().Get()`
2. GET amoCRM account to retrieve Drive URL
3. Create upload session: `POST {driveUrl}/v1.0/sessions`
4. Upload file in chunks (respects `max_part_size`)
5. Extract download URL from response
6. Return download URL

**Returns**: `string` - Download URL, or `null` on failure

**Upload Session Request**:
```json
{
  "file_name": "call_recording_AJnTczHyq8nAts1A.mp3",
  "file_size": 123456,
  "content_type": "audio/mpeg"
}
```

**Chunked Upload**:
- Uploads file in parts (max size from session response)
- Each part: `POST {upload_url}` with `Content-Type: audio/mpeg`
- Continues until `next_url` not present in response

**Final Response**:
```json
{
  "uuid": "file-uuid-here",
  "_links": {
    "download": {
      "href": "https://drive.amocrm.ru/download/uuid-here"
    }
  }
}
```

---

### SubscriptionService

**Namespace**: `RingCentral_amoCRM.Helpers`

**Lifetime**: Scoped

**Purpose**: Manages RingCentral WebHook subscriptions.

#### Constructor

```csharp
public SubscriptionService(
    RestClient rc,
    ILogger<SubscriptionService> logger,
    IConfiguration configuration)
```

#### Methods

##### `CreateSmsSubscriptionAsync()`

```csharp
public async Task<string> CreateSmsSubscriptionAsync()
```

**Purpose**: Creates WebHook subscription for SMS events.

**Process**:
1. Authorize with RingCentral
2. List all extensions
3. Delete existing subscriptions (cleanup)
4. Create new subscription with SMS event filters
5. Return subscription ID

**Event Filter Format**:
```
/restapi/v1.0/account/~/extension/{extensionId}/message-store/instant?type=SMS
```

**Subscription Properties**:
- `transportType`: "WebHook"
- `address`: `{RedirectUri}/api/RingCentralWebHook/webhook`
- `expiresIn`: 518400 seconds (6 days)

**Returns**: `string` - Subscription ID, or `null` on failure

##### `CreateCallSubscriptionAsync()`

```csharp
public async Task<string> CreateCallSubscriptionAsync()
```

**Purpose**: Creates WebHook subscription for call events (telephony sessions).

**Note**: Currently not actively used, as call processing happens via polling.

**Event Filter Format**:
```
/restapi/v1.0/account/~/extension/{extensionId}/telephony/sessions
```

**Returns**: `string` - Subscription ID, or `null` on failure

##### `PollCallLogsAsync()`

```csharp
public async Task PollCallLogsAsync()
```

**Purpose**: Fetches call logs from RingCentral API (used by `CallLogPollingService`).

**Process**:
1. Authorize
2. Call `_rc.Restapi().Account().CallLog().List()`
3. Log count of records retrieved

**Returns**: `Task` (void async)

---

### CallLogPollingService

**Namespace**: `RingCentral_amoCRM.Helpers`

**Lifetime**: Singleton (Hosted Service)

**Purpose**: Background service that continuously polls RingCentral for new call logs.

#### Configuration

```csharp
private readonly TimeSpan _pollInterval = TimeSpan.FromMinutes(2);
```

- Polls every 2 minutes
- Startup delay: 10 seconds

#### Constructor

```csharp
public CallLogPollingService(
    IServiceProvider serviceProvider,
    ILogger<CallLogPollingService> logger,
    RestClient rc,
    IConfiguration configuration,
    AmoCrmService amoService)
```

#### Main Loop

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
```

**Process**:
1. Wait 10 seconds (startup delay)
2. Loop until cancellation requested:
   - Authorize with RingCentral and amoCRM
   - Call `ProcessCallLogsAsync()`
   - Wait 2 minutes
   - Repeat

#### Private Methods

##### `EnsureAuthorized()`

Authorizes both RingCentral and amoCRM services.

##### `ProcessCallLogsAsync()`

**Process**:
1. Fetch call logs: `_rc.Restapi().Account().CallLog().List()`
2. For each record:
   - Extract phone number, direction, result, duration
   - Find leads in amoCRM by phone
   - If recording exists, upload to amoCRM Drive
   - Create call note with details and recording link

##### `ProcessCallRecordAsync()`

```csharp
private async Task ProcessCallRecordAsync(dynamic record)
```

**Parameters**:
- `record` (dynamic): RingCentral call log record

**Process**:
1. Extract call details
2. Find leads by phone number
3. For each lead:
   - Upload recording (if exists)
   - Create note with formatted call details

**Note Content Example**:
```
?? Звонок Outbound
От: +12397442122
Кому: +13152634256
Статус: Call connected
Длительность: 52с
Время: 2025-11-28T18:27:34.245Z
```

---

### SubscriptionHostedService

**Namespace**: `RingCentral_amoCRM.Helpers`

**Lifetime**: Singleton (Hosted Service)

**Purpose**: Background service that maintains active RingCentral WebHook subscriptions.

#### Configuration

```csharp
private readonly TimeSpan _subscriptionRenewalInterval = TimeSpan.FromDays(5);
private DateTime _lastSubscriptionRenewal = DateTime.MinValue;
```

- Renews subscriptions every 5 days
- Checks every 1 hour
- Startup delay: 5 seconds

#### Constructor

```csharp
public SubscriptionHostedService(
    IServiceProvider serviceProvider,
    ILogger<SubscriptionHostedService> logger)
```

#### Main Loop

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
```

**Process**:
1. Wait 5 seconds (startup delay)
2. Loop until cancellation requested:
   - Check if 5 days passed since last renewal
   - If yes:
     - Create/renew SMS subscription
     - Update `_lastSubscriptionRenewal`
   - Wait 1 hour
   - Repeat

**Why 5 days?**
- RingCentral subscriptions expire after 7 days
- Renewing at 5 days provides 2-day safety buffer
- Prevents subscription gaps

---

## Models

### RingCentralNotification

**File**: `Models/Model.cs`

**Purpose**: Represents WebHook notification from RingCentral for SMS events.

```csharp
public class RingCentralNotification
{
    public string Uuid { get; set; }
    public string Event { get; set; }
    public DateTime Timestamp { get; set; }
    public string SubscriptionId { get; set; }
    public string OwnerId { get; set; }
    public MessageBody Body { get; set; }
}
```

### MessageBody

```csharp
public class MessageBody
{
    public string Id { get; set; }
    public List<PartyInfo> To { get; set; }
    public PartyInfo From { get; set; }
    public string Type { get; set; } // "SMS"
    public DateTime CreationTime { get; set; }
    public string Direction { get; set; } // "Inbound" / "Outbound"
    public string Subject { get; set; } // SMS text
    public List<Attachment> Attachments { get; set; }
    public OwnerInfo Owner { get; set; }
}
```

### CallLogRecord

**File**: `Models/CallLogRecord.cs`

**Purpose**: Represents a call log entry from RingCentral API.

```csharp
public class CallLogRecord
{
    public string Id { get; set; }
    public string SessionId { get; set; }
    public DateTime StartTime { get; set; }
    public int Duration { get; set; }
    public string Type { get; set; } // "Voice"
    public string Direction { get; set; } // "Inbound" / "Outbound"
    public string Result { get; set; } // "Call connected"
    public CallParty To { get; set; }
    public CallParty From { get; set; }
    public RecordingInfo Recording { get; set; }
}
```

### AmoCrmToken

**File**: `Models/AmoCrmToken.cs`

**Purpose**: OAuth2 token response from amoCRM.

```csharp
public class AmoCrmToken
{
    public string AccessToken { get; set; }
    public string RefreshToken { get; set; }
    public int ExpiresIn { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    
    public void SetExpiration()
    {
        ExpiresAtUtc = DateTime.UtcNow.AddSeconds(ExpiresIn - 60);
    }
    
    public bool IsExpired()
    {
        return DateTime.UtcNow >= ExpiresAtUtc;
    }
}
```

### AmoCrmContactsResponse

**File**: `Models/AmoContactModel.cs`

**Purpose**: Response from amoCRM contacts API.

```csharp
public class AmoCrmContactsResponse
{
    public AmoCrmEmbedded Embedded { get; set; }
}

public class AmoCrmContact
{
    public long Id { get; set; }
    public string Name { get; set; }
    public AmoCrmContactEmbedded Embedded { get; set; }
}

public class AmoCrmLead
{
    public long Id { get; set; }
}
```

---

## Error Codes and Handling

### Common HTTP Status Codes

| Code | Meaning | Typical Cause |
|------|---------|---------------|
| 200 | OK | Request successful |
| 201 | Created | Note created successfully |
| 204 | No Content | No leads found for phone number |
| 400 | Bad Request | Invalid request parameters |
| 401 | Unauthorized | Token expired or invalid |
| 404 | Not Found | Resource doesn't exist |
| 429 | Too Many Requests | Rate limit exceeded |
| 500 | Internal Server Error | Unexpected error |

### Retry Strategy

The application implements retry logic for:

1. **amoCRM Token Refresh**:
   - 3 attempts
   - Exponential backoff (1s, 2s, 4s)
   - Logs all attempts

2. **Background Services**:
   - Infinite retry (catches exceptions and continues)
   - Logs errors without stopping service

### Exception Handling

All external API calls are wrapped in try-catch blocks:

```csharp
try
{
    // API call
}
catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
{
    // Expected during shutdown
    break;
}
catch (Exception ex)
{
    _logger.LogError(ex, "Description");
    // Continue or return null
}
```

---

## Configuration Reference

### appsettings.json Structure

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  
  "Credentials": {
    "CID": "RingCentral Client ID",
    "CS": "RingCentral Client Secret",
    "JWT": "RingCentral JWT Token",
    "URL": "https://platform.ringcentral.com",
    "RedirectUri": "Base URL for webhooks"
  },
  
  "AmoCrm": {
    "ClientId": "amoCRM Integration Client ID",
    "ClientSecret": "amoCRM Integration Secret",
    "RedirectUri": "OAuth callback URL",
    "Subdomain": "your_subdomain",
    "RefreshToken": "Auto-updated",
    "AccessToken": "Auto-updated"
  }
}
```

### Environment Variables (Alternative)

Can be set instead of appsettings.json:

```bash
Credentials__CID=your_client_id
Credentials__CS=your_client_secret
Credentials__JWT=your_jwt_token
AmoCrm__ClientId=your_amocrm_client_id
AmoCrm__ClientSecret=your_amocrm_secret
```

---

## Rate Limits

### RingCentral API

- **Light**: 1800 requests per minute per user
- **Medium**: 180 requests per minute per user
- **Heavy**: 60 requests per minute per user

**This Application's Usage**:
- Call log polling: 1 request every 2 minutes (Light)
- Subscription renewal: 1 request every 5 days (Light)
- WebHook endpoints: No rate limit (push-based)

### amoCRM API

- **Standard**: 15 requests per second per account
- **Token Refresh**: 20 requests per day per account

**This Application's Usage**:
- Contact search: 1 request per SMS/call event
- Note creation: 1 request per lead per event
- Token refresh: ~1 request per hour (auto-refresh)

---

## Best Practices

1. **Always check logs** when troubleshooting
2. **Never expose credentials** in version control
3. **Use HTTPS** for all WebHook endpoints
4. **Monitor subscription status** regularly
5. **Backup configuration** before updates
6. **Test in staging** before production deployment
7. **Set up alerts** for repeated errors
8. **Document custom changes** in this file

---

## Glossary

- **WebHook**: HTTP callback triggered by events (push notification)
- **Polling**: Periodic API requests to check for new data
- **JWT**: JSON Web Token (authentication method)
- **OAuth2**: Open standard for authorization
- **Refresh Token**: Long-lived token used to obtain new access tokens
- **Access Token**: Short-lived token for API authentication
- **Subscription**: RingCentral's WebHook configuration
- **Extension**: RingCentral user or device with phone number
- **Lead**: Sales opportunity in amoCRM
- **Contact**: Person or company in amoCRM
- **Note**: Activity record attached to lead/contact in amoCRM

---

*Last Updated: 2025-01-15*
