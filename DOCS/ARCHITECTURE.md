# Architecture Documentation

## System Architecture Overview

This document provides a comprehensive architectural overview of the RingCentral to amoCRM integration application.

---

## Table of Contents

1. [High-Level Architecture](#high-level-architecture)
2. [Component Diagram](#component-diagram)
3. [Data Flow Diagrams](#data-flow-diagrams)
4. [Sequence Diagrams](#sequence-diagrams)
5. [Design Patterns](#design-patterns)
6. [Technology Stack](#technology-stack)
7. [Security Architecture](#security-architecture)
8. [Scalability Considerations](#scalability-considerations)

---

## High-Level Architecture

### System Context

```
┌──────────────────────────────────────────────────────────────────────────┐
│                          External Systems                                 │
│                                                                           │
│  ┌─────────────────────┐                  ┌─────────────────────┐       │
│  │  RingCentral        │                  │     amoCRM          │       │
│  │  Platform           │                  │     Platform        │       │
│  │                     │                  │                     │       │
│  │  - SMS Events       │                  │  - Leads API        │       │
│  │  - Call Logs API    │                  │  - Contacts API     │       │
│  │  - Recordings API   │                  │  - Notes API        │       │
│  │  - WebHooks         │                  │  - Drive API        │       │
│  └──────────┬──────────┘                  └──────────┬──────────┘       │
│             │                                        │                   │
│             │ Push Events (WebHook)                  │ REST API          │
│             │ Pull Data (Polling)                    │ (HTTPS)           │
│             │                                        │                   │
└─────────────┼────────────────────────────────────────┼───────────────────┘
              │                                        │
              │                                        │
              ▼                                        ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                    Integration Application                                │
│                   (ASP.NET Core 9.0 - C# 13)                             │
│                                                                           │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │                      Presentation Layer                            │ │
│  │  ┌──────────────────┐  ┌──────────────────┐                       │ │
│  │  │  WebHook         │  │  OAuth           │                       │ │
│  │  │  Controllers     │  │  Controllers     │                       │ │
│  │  └──────────────────┘  └──────────────────┘                       │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                           │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │                      Business Logic Layer                          │ │
│  │  ┌──────────────────────────────────────────────────────────────┐ │ │
│  │  │  AmoCRM Service                                              │ │ │
│  │  │  - Token Management                                          │ │ │
│  │  │  - Lead Search                                               │ │ │
│  │  │  - Note Creation                                             │ │ │
│  │  │  - Recording Upload                                          │ │ │
│  │  └──────────────────────────────────────────────────────────────┘ │ │
│  │                                                                    │ │
│  │  ┌──────────────────────────────────────────────────────────────┐ │ │
│  │  │  Subscription Service                                        │ │ │
│  │  │  - WebHook Subscription Management                           │ │ │
│  │  │  - RingCentral Authorization                                 │ │ │
│  │  └──────────────────────────────────────────────────────────────┘ │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                           │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │                    Background Services Layer                       │ │
│  │  ┌─────────────────────┐  ┌────────────────────────┐             │ │
│  │  │  Subscription       │  │  Call Log              │             │ │
│  │  │  Hosted Service     │  │  Polling Service       │             │ │
│  │  │  (Renewal Timer)    │  │  (2-min Timer)         │             │ │
│  │  └─────────────────────┘  └────────────────────────┘             │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                           │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │                      Infrastructure Layer                          │ │
│  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐            │ │
│  │  │  HTTP Client │  │  RingCentral │  │  Logging     │            │ │
│  │  │  Factory     │  │  SDK Client  │  │  System      │            │ │
│  │  └──────────────┘  └──────────────┘  └──────────────┘            │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                           │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │                      Configuration                                 │ │
│  │  - appsettings.json                                                │ │
│  │  - Environment Variables                                           │ │
│  └────────────────────────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────────────┘
```

---

## Component Diagram

### Detailed Component Relationships

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          ASP.NET Core Application                           │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│                              Controllers                                     │
│                                                                              │
│  ┌──────────────────────────────┐      ┌─────────────────────────────┐     │
│  │ RingCentralWebHookController │      │   AmoCrmController          │     │
│  │                              │      │                             │     │
│  │ - HandleWebHookOrValidation()│      │ - Callback(code)            │     │
│  │ - HandleCallWebHook()        │      │                             │     │
│  │ - GetSubscriptions()         │      │                             │     │
│  │ - DeleteSub()                │      │                             │     │
│  └────────────┬─────────────────┘      └──────────────┬──────────────┘     │
│               │                                       │                     │
│               │ Uses                                  │ Uses                │
│               ▼                                       ▼                     │
└───────────────┼───────────────────────────────────────┼─────────────────────┘
                │                                       │
                │                                       │
┌───────────────┼───────────────────────────────────────┼─────────────────────┐
│               │              Services                 │                     │
│               │                                       │                     │
│               ▼                                       ▼                     │
│  ┌────────────────────────────┐         ┌─────────────────────────────┐   │
│  │    AmoCrmService           │◄────────┤   SubscriptionService       │   │
│  │    (Singleton)             │         │   (Scoped)                  │   │
│  │                            │         │                             │   │
│  │ - InitializeAsync()        │         │ - CreateSmsSubscription()   │   │
│  │ - GetAccessTokenAsync()    │         │ - CreateCallSubscription()  │   │
│  │ - RefreshTokensAsync()     │         │ - PollCallLogsAsync()       │   │
│  │ - FindLeadByPhoneNumber()  │         │                             │   │
│  │ - CreateNoteAsync()        │         └─────────────┬───────────────┘   │
│  │ - CreateCallNoteAsync()    │                       │                   │
│  │ - UploadCallRecording()    │                       │ Used by           │
│  └──────────┬─────────────────┘                       │                   │
│             │                                          │                   │
│             │ Uses                                     ▼                   │
│             ▼                         ┌─────────────────────────────────┐ │
│  ┌────────────────────┐               │  SubscriptionHostedService      │ │
│  │  HttpClient        │               │  (Singleton - BackgroundService)│ │
│  │  (via Factory)     │               │                                 │ │
│  │                    │               │ - Renews subscriptions every    │ │
│  │ - Configured for   │               │   5 days                        │ │
│  │   amoCRM API       │               │ - Checks every 1 hour           │ │
│  │ - TLS 1.2          │               └─────────────────────────────────┘ │
│  │ - Custom SSL       │                                                   │
│  │   validation       │               ┌─────────────────────────────────┐ │
│  └────────────────────┘               │  CallLogPollingService          │ │
│                                       │  (Singleton - BackgroundService)│ │
│             ▲                         │                                 │ │
│             │                         │ - Polls call logs every 2 min   │ │
│             │ Uses                    │ - Processes recordings          │ │
│             │                         │ - Creates call notes            │ │
│  ┌────────────────────┐               └─────────────┬───────────────────┘ │
│  │  RestClient        │                             │                     │
│  │  (Singleton)       │                             │ Uses                │
│  │                    │                             ▼                     │
│  │ - RingCentral SDK  │◄────────────────────────────┘                     │
│  │ - JWT Auth         │                                                   │
│  │ - API Wrapper      │                                                   │
│  └────────────────────┘                                                   │
│                                                                            │
└────────────────────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────────────────────┐
│                              Data Models                                    │
│                                                                             │
│  ┌──────────────────────┐  ┌──────────────────────┐  ┌─────────────────┐ │
│  │ RingCentralNotification│  │  CallLogRecord       │  │  AmoCrmToken    │ │
│  │ - SMS events         │  │  - Call log entries  │  │  - OAuth tokens │ │
│  └──────────────────────┘  └──────────────────────┘  └─────────────────┘ │
│                                                                             │
│  ┌──────────────────────┐  ┌──────────────────────┐                       │
│  │ AmoCrmContactsResponse│  │  MessageBody         │                       │
│  │ - Search results     │  │  - SMS content       │                       │
│  └──────────────────────┘  └──────────────────────┘                       │
└────────────────────────────────────────────────────────────────────────────┘
```

---

## Data Flow Diagrams

### 1. SMS WebHook Flow

```
┌─────────────┐
│ RingCentral │ User sends SMS
│             ├──────────────────────────────────┐
└─────────────┘                                  │
                                                 │
                                                 ▼
                                    ┌─────────────────────────┐
                                    │ RingCentral triggers    │
                                    │ WebHook subscription    │
                                    └───────────┬─────────────┘
                                                │
                                                │ HTTP POST
                                                │ JSON payload
                                                ▼
                              ┌─────────────────────────────────┐
                              │ Integration Application         │
                              │ POST /api/RingCentralWebHook/   │
                              │      webhook                    │
                              └───────────┬─────────────────────┘
                                          │
                                          ▼
                              ┌─────────────────────────────────┐
                              │ RingCentralWebHookController    │
                              │ - Deserialize notification      │
                              │ - Extract SMS details           │
                              └───────────┬─────────────────────┘
                                          │
                                          ▼
                              ┌─────────────────────────────────┐
                              │ AmoCrmService                   │
                              │ .FindLeadByPhoneNumberAsync()   │
                              └───────────┬─────────────────────┘
                                          │
                                          │ GET /api/v4/contacts?query=...
                                          ▼
                              ┌─────────────────────────────────┐
                              │ amoCRM API                      │
                              │ Returns: Lead IDs               │
                              └───────────┬─────────────────────┘
                                          │
                                          ▼
                              ┌─────────────────────────────────┐
                              │ For each Lead ID:               │
                              │ AmoCrmService.CreateNoteAsync() │
                              └───────────┬─────────────────────┘
                                          │
                                          │ POST /api/v4/leads/{id}/notes
                                          ▼
                              ┌─────────────────────────────────┐
                              │ amoCRM API                      │
                              │ Creates note type: "sms_in"     │
                              └─────────────────────────────────┘
```

### 2. Call Log Polling Flow

```
┌─────────────────────────┐
│ CallLogPollingService   │ Timer triggers (every 2 min)
│ (Background Service)    │
└───────────┬─────────────┘
            │
            │ 1. Authorize
            ▼
┌─────────────────────────────────┐
│ RingCentral RestClient          │
│ JWT Authorization               │
└───────────┬─────────────────────┘
            │
            │ 2. Fetch call logs
            ▼
┌─────────────────────────────────┐
│ RingCentral API                 │
│ GET /restapi/v1.0/account/~     │
│     /call-log                   │
└───────────┬─────────────────────┘
            │
            │ Returns: CallLogRecord[]
            ▼
┌─────────────────────────────────┐
│ Process each record:            │
│ - Extract phone, duration, etc. │
│ - Check if recording exists     │
└───────────┬─────────────────────┘
            │
            ▼
┌─────────────────────────────────┐
│ AmoCrmService                   │
│ .FindLeadByPhoneNumberAsync()   │
└───────────┬─────────────────────┘
            │
            ▼
┌─────────────────────────────────┐
│ amoCRM API                      │
│ GET /api/v4/contacts?query=...  │
│ Returns: Lead IDs               │
└───────────┬─────────────────────┘
            │
            ▼
┌─────────────────────────────────┐
│ If recording exists:            │
│ AmoCrmService                   │
│ .UploadCallRecordingAsync()     │
└───────────┬─────────────────────┘
            │
            │ a. Download from RingCentral
            ▼
┌─────────────────────────────────┐
│ RingCentral API                 │
│ GET /restapi/v1.0/account/~     │
│     /recording/{id}/content     │
│ Returns: Binary audio data      │
└───────────┬─────────────────────┘
            │
            │ b. Get Drive URL
            ▼
┌─────────────────────────────────┐
│ amoCRM API                      │
│ GET /api/v4/account?            │
│     with=drive_url              │
│ Returns: drive_url              │
└───────────┬─────────────────────┘
            │
            │ c. Create upload session
            ▼
┌─────────────────────────────────┐
│ amoCRM Drive API                │
│ POST {drive_url}/v1.0/sessions  │
│ Returns: upload_url, max_part   │
└───────────┬─────────────────────┘
            │
            │ d. Upload in chunks
            ▼
┌─────────────────────────────────┐
│ amoCRM Drive API                │
│ POST {upload_url}               │
│ (chunked binary upload)         │
│ Returns: uuid, download link    │
└───────────┬─────────────────────┘
            │
            │ e. Create call note
            ▼
┌─────────────────────────────────┐
│ AmoCrmService                   │
│ .CreateCallNoteAsync()          │
│ - Include recording link        │
└───────────┬─────────────────────┘
            │
            │ POST /api/v4/leads/{id}/notes
            ▼
┌─────────────────────────────────┐
│ amoCRM API                      │
│ Creates note type: "call_in" or │
│ "call_out" with recording       │
└─────────────────────────────────┘
```

### 3. Subscription Renewal Flow

```
┌─────────────────────────────┐
│ SubscriptionHostedService   │ Timer: Check every 1 hour
│ (Background Service)        │
└───────────┬─────────────────┘
            │
            │ Check: now - lastRenewal >= 5 days?
            ▼
      ┌─────────────┐
      │  Yes / No   │
      └─────┬───┬───┘
            │   │
           Yes  No ────► Wait 1 hour, repeat
            │
            ▼
┌─────────────────────────────────┐
│ Create scope (DI)               │
│ Get SubscriptionService         │
└───────────┬─────────────────────┘
            │
            │ 1. Authorize RingCentral
            ▼
┌─────────────────────────────────┐
│ RingCentral RestClient          │
│ JWT Authorization               │
└───────────┬─────────────────────┘
            │
            │ 2. Get all extensions
            ▼
┌─────────────────────────────────┐
│ RingCentral API                 │
│ GET /restapi/v1.0/account/~     │
│     /extension                  │
│ Returns: Extension IDs          │
└───────────┬─────────────────────┘
            │
            │ 3. Delete old subscriptions
            ▼
┌─────────────────────────────────┐
│ RingCentral API                 │
│ DELETE /restapi/v1.0/           │
│        subscription/{id}        │
│ (for each existing)             │
└───────────┬─────────────────────┘
            │
            │ 4. Create new subscription
            ▼
┌─────────────────────────────────┐
│ SubscriptionService             │
│ .CreateSmsSubscriptionAsync()   │
│ - Event filters: message-store  │
│ - Delivery: WebHook             │
│ - Expires: 6 days               │
└───────────┬─────────────────────┘
            │
            │ POST /restapi/v1.0/subscription
            ▼
┌─────────────────────────────────┐
│ RingCentral API                 │
│ Creates subscription            │
│ Returns: subscription ID        │
└───────────┬─────────────────────┘
            │
            │ 5. Update lastRenewal
            ▼
┌─────────────────────────────────┐
│ SubscriptionHostedService       │
│ _lastSubscriptionRenewal = now  │
└─────────────────────────────────┘
            │
            │ Wait 1 hour, repeat
            ▼
```

---

## Sequence Diagrams

### SMS Note Creation Sequence

```
User          RingCentral     Integration App          amoCRM API
 │                  │                 │                     │
 │──Send SMS───────►│                 │                     │
 │                  │                 │                     │
 │                  │──WebHook POST──►│                     │
 │                  │  (notification) │                     │
 │                  │                 │                     │
 │                  │                 │──Deserialize JSON──►│
 │                  │                 │                     │
 │                  │                 │──Search Contact────►│
 │                  │                 │  GET /contacts?     │
 │                  │                 │      query={phone}  │
 │                  │                 │                     │
 │                  │                 │◄────Return Leads────│
 │                  │                 │     [leadId: 123]   │
 │                  │                 │                     │
 │                  │                 │──Create Note───────►│
 │                  │                 │  POST /leads/123    │
 │                  │                 │       /notes        │
 │                  │                 │  {type: "sms_in"}   │
 │                  │                 │                     │
 │                  │                 │◄────201 Created─────│
 │                  │                 │                     │
 │                  │◄────200 OK──────│                     │
 │                  │                 │                     │
```

### Call Recording Upload Sequence

```
Timer      Integration App      RingCentral API     amoCRM API
 │                │                    │                   │
 │──2 min────────►│                    │                   │
 │   elapsed      │                    │                   │
 │                │                    │                   │
 │                │──Get Call Logs────►│                   │
 │                │  /call-log         │                   │
 │                │                    │                   │
 │                │◄───Return Records──│                   │
 │                │   [{id, recording}]│                   │
 │                │                    │                   │
 │                │──Download─────────►│                   │
 │                │  Recording         │                   │
 │                │  /recording/       │                   │
 │                │   {id}/content     │                   │
 │                │                    │                   │
 │                │◄───Binary Data─────│                   │
 │                │   (audio/mpeg)     │                   │
 │                │                    │                   │
 │                │──Get Drive URL─────────────────────────►│
 │                │                              /account?  │
 │                │                              with=drive │
 │                │                                         │
 │                │◄────Return URL──────────────────────────│
 │                │    {drive_url}                          │
 │                │                                         │
 │                │──Create Upload Session──────────────────►│
 │                │  POST {drive_url}/v1.0/sessions         │
 │                │  {file_name, file_size}                 │
 │                │                                         │
 │                │◄────Session Created─────────────────────│
 │                │    {upload_url, max_part_size}          │
 │                │                                         │
 │                │──Upload Part 1──────────────────────────►│
 │                │  POST {upload_url}                      │
 │                │  (binary chunk)                         │
 │                │                                         │
 │                │◄────Next URL────────────────────────────│
 │                │    {next_url}                           │
 │                │                                         │
 │                │──Upload Part 2──────────────────────────►│
 │                │  POST {next_url}                        │
 │                │  (binary chunk)                         │
 │                │                                         │
 │                │◄────Complete─────────────────────────────│
 │                │    {uuid, download_link}                │
 │                │                                         │
 │                │──Create Call Note───────────────────────►│
 │                │  POST /leads/{id}/notes                 │
 │                │  {type: "call_in", link}                │
 │                │                                         │
 │                │◄────201 Created─────────────────────────│
 │                │                                         │
```

### OAuth2 Token Refresh Sequence

```
Background      AmoCrmService        amoCRM API       Config File
Service               │                   │                │
 │                    │                   │                │
 │──Need Token───────►│                   │                │
 │                    │                   │                │
 │                    │──Check Expiry────►│                │
 │                    │  (in memory)      │                │
 │                    │                   │                │
 │                    │◄──Expired─────────│                │
 │                    │                   │                │
 │                    │──Read Refresh─────────────────────►│
 │                    │  Token            │                │
 │                    │                   │                │
 │                    │◄──────────────────────────────────│
 │                    │  {refresh_token}  │                │
 │                    │                   │                │
 │                    │──POST Token───────►│                │
 │                    │  /oauth2/         │                │
 │                    │  access_token     │                │
 │                    │  grant_type=      │                │
 │                    │  refresh_token    │                │
 │                    │                   │                │
 │                    │◄──New Tokens──────│                │
 │                    │  {access_token,   │                │
 │                    │   refresh_token,  │                │
 │                    │   expires_in}     │                │
 │                    │                   │                │
 │                    │──Update Memory────►│                │
 │                    │  _currentToken    │                │
 │                    │                   │                │
 │                    │──Update File──────────────────────►│
 │                    │  appsettings.json │                │
 │                    │                   │                │
 │◄───Return Token────│                   │                │
 │   (valid)          │                   │                │
```

---

## Design Patterns

### 1. Dependency Injection (DI)

**Pattern**: Constructor Injection with Microsoft.Extensions.DependencyInjection

**Usage**:
```csharp
// Registration (Program.cs)
builder.Services.AddSingleton<RestClient>();
builder.Services.AddSingleton<AmoCrmService>();
builder.Services.AddScoped<SubscriptionService>();
builder.Services.AddHostedService<CallLogPollingService>();

// Injection (AmoCrmService)
public AmoCrmService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<AmoCrmService> logger,
    RestClient rc)
{
    // Dependencies injected automatically
}
```

**Benefits**:
- Testability (mock dependencies)
- Loose coupling
- Lifecycle management

### 2. Factory Pattern

**Pattern**: HttpClientFactory

**Usage**:
```csharp
builder.Services.AddHttpClient("AmoCrmClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.BaseAddress = new Uri(baseUrl);
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    return new HttpClientHandler
    {
        SslProtocols = SslProtocols.Tls12,
        // ... custom configuration
    };
});
```

**Benefits**:
- Proper HttpClient lifecycle management
- Connection pooling
- Custom handler configuration

### 3. Hosted Service Pattern

**Pattern**: IHostedService / BackgroundService

**Usage**:
```csharp
public class CallLogPollingService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Background work
            await Task.Delay(_pollInterval, stoppingToken);
        }
    }
}
```

**Benefits**:
- Automatic startup/shutdown
- Graceful cancellation
- Long-running tasks

### 4. Retry Pattern

**Pattern**: Exponential Backoff

**Usage**:
```csharp
int maxRetries = 3;
int retryDelayMs = 1000;

for (int attempt = 1; attempt <= maxRetries; attempt++)
{
    try
    {
        await operation();
        return; // Success
    }
    catch (HttpRequestException ex) when (attempt < maxRetries)
    {
        await Task.Delay(retryDelayMs);
        retryDelayMs *= 2; // Exponential backoff
    }
}
```

**Benefits**:
- Handles transient failures
- Prevents API overload
- Increases reliability

### 5. Service Locator Pattern (Scoped Services in Singletons)

**Pattern**: IServiceProvider for creating scopes

**Usage**:
```csharp
// In singleton hosted service
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var service = scope.ServiceProvider
                .GetRequiredService<SubscriptionService>();
            await service.DoWork();
        }
    }
}
```

**Benefits**:
- Allows singletons to use scoped services safely
- Proper disposal of scoped dependencies

### 6. Strategy Pattern (Implicit)

**Pattern**: Different note creation strategies

**Usage**:
```csharp
// SMS note
await AmoCrmService.CreateNoteAsync(leadId, text, phone);
// Note type: "sms_in"

// Call note
await AmoCrmService.CreateCallNoteAsync(leadId, record, recordingUrl);
// Note type: "call_in" or "call_out"
```

**Benefits**:
- Different note formats for different event types
- Easily extensible for new event types

---

## Technology Stack

### Core Technologies

| Layer | Technology | Version | Purpose |
|-------|-----------|---------|---------|
| **Runtime** | .NET | 9.0 | Application platform |
| **Language** | C# | 13.0 | Programming language |
| **Framework** | ASP.NET Core | 9.0 | Web framework |
| **Server** | Kestrel | Built-in | HTTP server |

### Libraries and SDKs

| Library | Version | Purpose |
|---------|---------|---------|
| RingCentral.Net | Latest | RingCentral API SDK |
| System.Text.Json | Built-in | JSON serialization |
| Newtonsoft.Json | Latest | Legacy JSON support |
| Microsoft.Extensions.Hosting | Built-in | Background services |
| Microsoft.Extensions.Http | Built-in | HTTP client factory |
| Microsoft.Extensions.Logging | Built-in | Logging infrastructure |

### External APIs

| Service | Protocol | Authentication |
|---------|----------|----------------|
| RingCentral API | REST/HTTPS | JWT |
| amoCRM API | REST/HTTPS | OAuth 2.0 |

---

## Security Architecture

### Authentication Flow

```
┌─────────────────────────────────────────────────────────────┐
│                 Authentication Mechanisms                    │
└─────────────────────────────────────────────────────────────┘

┌──────────────────────┐          ┌──────────────────────┐
│  RingCentral Auth    │          │  amoCRM Auth         │
│  (JWT)               │          │  (OAuth 2.0)         │
└──────────────────────┘          └──────────────────────┘
         │                                 │
         │ Long-lived token                │ Refresh token flow
         │ Stored in config                │ Access token expires
         │                                 │ every 24 hours
         ▼                                 ▼
┌──────────────────────┐          ┌──────────────────────┐
│  RestClient          │          │  AmoCrmService       │
│  - Authorize(JWT)    │          │  - RefreshTokens()   │
│  - Auto-refresh      │          │  - Auto-update       │
└──────────────────────┘          └──────────────────────┘
```

### Data Security

1. **Credentials Storage**:
   - Stored in `appsettings.json` (not committed to Git)
   - Can use environment variables for production
   - Consider Azure Key Vault for enterprise deployments

2. **Transmission Security**:
   - All API calls over HTTPS
   - TLS 1.2 minimum
   - Certificate validation (customizable for dev)

3. **Token Security**:
   - Access tokens stored in memory only
   - Refresh tokens persisted to disk (encrypted file system recommended)
   - Automatic rotation on refresh

4. **WebHook Validation**:
   - Validation token handshake
   - HTTPS endpoint required by RingCentral
   - Consider adding HMAC signature verification (future enhancement)

### Access Control

```
┌─────────────────────────────────────────────────────────────┐
│              No Built-in Access Control                      │
│  (Application acts as a trusted service)                     │
└─────────────────────────────────────────────────────────────┘

 Recommended for Production:
 ┌──────────────────────────────────────────────────────────┐
 │  - Deploy behind firewall or VPN                         │
 │  - Use API Gateway with authentication                   │
 │  - Restrict network access to RingCentral/amoCRM IPs     │
 │  - Add admin authentication for utility endpoints        │
 └──────────────────────────────────────────────────────────┘
```

---

## Scalability Considerations

### Current Architecture Limitations

1. **Single Instance**:
   - No distributed state management
   - Background services run on one instance only
   - `_lastSubscriptionRenewal` stored in memory

2. **API Rate Limits**:
   - RingCentral: 1800 requests/min (Light endpoints)
   - amoCRM: 15 requests/second
   - Current usage well within limits

### Scaling Strategies

#### Horizontal Scaling (Future)

```
┌──────────────────────────────────────────────────────────────┐
│                    Load Balancer                              │
└────────────┬─────────────────────────────┬───────────────────┘
             │                             │
             ▼                             ▼
┌───────────────────────┐     ┌───────────────────────┐
│  Instance 1           │     │  Instance 2           │
│  - WebHook only       │     │  - WebHook only       │
│  - No background      │     │  - No background      │
└───────────────────────┘     └───────────────────────┘

             ┌───────────────────────────┐
             │  Instance 3 (Singleton)   │
             │  - Background services    │
             │  - Call log polling       │
             │  - Subscription renewal   │
             └───────────────────────────┘

      ┌──────────────────────────────────────┐
      │  Distributed State Store (Redis)     │
      │  - Last renewal timestamp            │
      │  - Processed call log IDs            │
      └──────────────────────────────────────┘
```

**Required Changes**:
- Move state to Redis or database
- Use distributed locking for background services
- Sticky sessions or shared token cache

#### Vertical Scaling

- Increase CPU/memory for single instance
- Current resource usage is minimal
- Sufficient for most deployments

### Performance Optimization

1. **Caching**:
   - Cache lead search results (TTL: 5 minutes)
   - Cache extension list (TTL: 1 hour)
   - Implement IMemoryCache or Redis

2. **Async Processing**:
   - Already fully async
   - Consider message queue for high-volume events

3. **Batch Processing**:
   - Process multiple call logs in parallel
   - Batch note creation requests (if amoCRM API supports)

4. **Connection Pooling**:
   - Already implemented via HttpClientFactory
   - Configure `MaxConnectionsPerServer` as needed

---

## Monitoring and Observability

### Logging Strategy

**Log Levels**:
- `Information`: Normal operations, successful actions
- `Warning`: Non-critical issues, retries
- `Error`: Failures requiring attention

**Structured Logging Example**:
```csharp
_logger.LogInformation(
    "Note added to lead {LeadId} for call from {PhoneNumber}",
    leadId,
    phoneNumber);
```

### Metrics to Monitor

1. **Application Health**:
   - Background service uptime
   - WebHook response times
   - Memory usage

2. **Business Metrics**:
   - SMS events processed per hour
   - Call logs processed per hour
   - Notes created successfully
   - Failed note creation rate

3. **API Metrics**:
   - RingCentral API errors
   - amoCRM API errors
   - Token refresh failures
   - WebHook validation failures

### Recommended Monitoring Tools

- **Application Insights** (Azure)
- **Prometheus + Grafana** (Self-hosted)
- **ELK Stack** (Elasticsearch, Logstash, Kibana)
- **Seq** (Structured logs)

---

## Deployment Architectures

### Simple Deployment

```
┌──────────────────────────────────────────────────────┐
│              Single Server                            │
│                                                       │
│  ┌────────────────────────────────────────────────┐ │
│  │  Integration Application                       │ │
│  │  - WebHook endpoints                           │ │
│  │  - Background services                         │ │
│  │  - All-in-one                                  │ │
│  └────────────────────────────────────────────────┘ │
│                                                       │
│  ┌────────────────────────────────────────────────┐ │
│  │  Kestrel Web Server                            │ │
│  │  - HTTPS on port 443                           │ │
│  └────────────────────────────────────────────────┘ │
│                                                       │
│  ┌────────────────────────────────────────────────┐ │
│  │  File System                                   │ │
│  │  - appsettings.json (tokens)                   │ │
│  └────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────┘

Suitable for: Small teams, <100 users
```

### Containerized Deployment

```
┌──────────────────────────────────────────────────────┐
│              Docker Host                              │
│                                                       │
│  ┌────────────────────────────────────────────────┐ │
│  │  Integration Container                         │ │
│  │  - ASP.NET Core Runtime                        │ │
│  │  - Application DLLs                            │ │
│  │  - Port 8080 (internal)                        │ │
│  └────────────────────────────────────────────────┘ │
│                                                       │
│  ┌────────────────────────────────────────────────┐ │
│  │  Reverse Proxy Container (nginx)               │ │
│  │  - SSL termination                             │ │
│  │  - Port 443 → 8080                             │ │
│  └────────────────────────────────────────────────┘ │
│                                                       │
│  ┌────────────────────────────────────────────────┐ │
│  │  Volume Mounts                                 │ │
│  │  - /app/appsettings.json                       │ │
│  │  - /app/logs                                   │ │
│  └────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────┘

Suitable for: Portable deployments, CI/CD
```

### Cloud Deployment (Azure Example)

```
                       ┌─────────────────────┐
                       │  Azure Front Door   │
                       │  or App Gateway     │
                       │  (SSL, WAF)         │
                       └──────────┬──────────┘
                                  │
                                  ▼
                  ┌───────────────────────────────┐
                  │  Azure App Service            │
                  │  - Linux/Windows              │
                  │  - Auto-scaling               │
                  │  - Always On                  │
                  └───────┬───────────────────────┘
                          │
              ┌───────────┼───────────┐
              │           │           │
              ▼           ▼           ▼
    ┌─────────────┐  ┌─────────────┐  ┌─────────────┐
    │ Key Vault   │  │ App Insights│  │ Log Analytics│
    │ (Secrets)   │  │ (Monitoring)│  │ (Logs)       │
    └─────────────┘  └─────────────┘  └──────────────┘

Suitable for: Enterprise, high availability
```

---

## Future Enhancements

### Potential Features

1. **Bidirectional Sync**:
   - Send SMS from amoCRM via RingCentral
   - Initiate calls from amoCRM UI

2. **Advanced Filtering**:
   - Only process calls > X duration
   - Filter by specific extensions
   - Skip internal calls

3. **Duplicate Detection**:
   - Track processed call IDs in database
   - Prevent duplicate notes

4. **Rich Media**:
   - Support MMS attachments
   - Voicemail transcription

5. **Dashboard**:
   - Real-time sync status
   - Error notifications
   - Manual retry UI

6. **Multi-Tenant**:
   - Support multiple amoCRM accounts
   - Per-tenant configuration

---

*This architecture documentation should be updated whenever significant changes are made to the system design.*

*Last Updated: 2025-01-15*
