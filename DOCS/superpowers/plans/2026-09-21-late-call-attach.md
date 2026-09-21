# Поздняя привязка звонков — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Звонок, для которого в момент основной обработки не нашлось
контакта/сделки в amoCRM, автоматически прикрепляется к сделке позже,
когда контакт со сделкой появится в amoCRM — без своей БД, без
вебхуков, без новых публичных эндпоинтов.

**Architecture:** Новый `BackgroundService` (`LateAttachService`) раз в
`LateAttach:IntervalMinutes` опрашивает amoCRM на изменённые контакты и
сделки, собирает их телефоны, сверяет с журналом звонков RingCentral за
`LateAttach:LookbackDays` и прогоняет совпавшие звонки через
переиспользуемую логику обработки (`AmoCrmService.ProcessSingleCallAsync`).
Гонка между основным поллингом и поздней привязкой закрывается общим
`CallProcessingGuard` (singleton, per-call-id lock + потокобезопасный
"processed" набор). Догоняющий проход при старте в существующем
`CallLogPollingService` закрывает окно простоя для уже существующих
контактов.

**Tech Stack:** .NET 9, ASP.NET Core (`BackgroundService`, `IConfiguration`),
`RingCentral.Net` SDK, amoCRM REST API v4 (`HttpClient`), `System.Text.Json`.

**Spec:** `DOCS/superpowers/specs/2026-09-21-late-call-attach-design.md`

## Global Constraints

- Публичный репозиторий: никаких токенов/секретов в коде, комментариях, логах.
- Живой `appsettings.json` на проде перезаписывается приложением — новые
  ключи туда не добавлять; конфигурация только через переменные окружения
  (`LateAttach__*`) с дефолтами в коде.
- `LateAttach:Enabled=false` полностью отключает позднюю привязку И
  догоняющий проход при старте (аварийный выключатель).
- Не менять поведение для звонков, которые находятся сразу основным поллингом.
- Не трогать `SubscriptionService.cs`, `SubscriptionHostedService.cs`,
  формат `appsettings.json`, существующий парсинг времени в `IsRecentCall`.
- SMS вне scope — обрабатываем только call_in/call_out.
- Никакой локальной БД или файлов состояния — всё в памяти процесса.
- Сборка `dotnet build -c Release` должна проходить без ошибок после
  каждого коммита.
- В проекте нет тестового проекта/фреймворка (только
  `RingCentral_amoCRM.csproj`, web SDK, без xunit/nunit) — проверка
  каждого шага идёт через `dotnet build -c Release` и, где отмечено,
  через ручной прогон логики (лог-вывод, ручной вызов метода из
  временного кода, который затем удаляется). Каждый шаг явно указывает
  способ проверки.

---

## Task 1: `CallProcessingGuard` — общая защита от гонки

**Files:**
- Create: `RingCentral_amoCRM/Helpers/CallProcessingGuard.cs`

**Interfaces:**
- Consumes: ничего (не зависит от других задач).
- Produces:
  - `class CallProcessingGuard`
  - `bool IsProcessed(string callId)`
  - `void MarkProcessed(string callId)`
  - `Task<IDisposable> AcquireAsync(string callId)`
  - `void ClearOlderThan(TimeSpan age)` — очищает `_processedCallIds` и
    `_locks`, если с последней очистки прошло больше `age` (вызывающий
    сам решает периодичность, как сейчас в `CallLogPollingService`).
  - Регистрируется в `Program.cs` как singleton (Task 6).

- [ ] **Step 1: Написать `CallProcessingGuard`**

```csharp
using System.Collections.Concurrent;

namespace RingCentral_amoCRM.Helpers;

// Общая защита от гонки между CallLogPollingService и LateAttachService:
// оба могут одновременно решить, что у звонка ещё нет заметки, и оба
// попытаются её создать. AcquireAsync сериализует блок "проверка +
// создание" по конкретному call ID; IsProcessed/MarkProcessed — быстрый
// потокобезопасный pre-check в памяти (источник истины при рестарте —
// amoCRM через NoteExistsAsync, не этот набор).
public class CallProcessingGuard
{
    private readonly ConcurrentDictionary<string, byte> _processedCallIds = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private DateTime _lastCleanup = DateTime.UtcNow;

    public bool IsProcessed(string callId) =>
        !string.IsNullOrEmpty(callId) && _processedCallIds.ContainsKey(callId);

    public void MarkProcessed(string callId)
    {
        if (!string.IsNullOrEmpty(callId))
        {
            _processedCallIds[callId] = 0;
        }
    }

    public async Task<IDisposable> AcquireAsync(string callId)
    {
        var sem = _locks.GetOrAdd(callId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        return new Releaser(sem);
    }

    public void ClearOlderThanIfDue(TimeSpan age)
    {
        if ((DateTime.UtcNow - _lastCleanup) < age)
        {
            return;
        }

        _processedCallIds.Clear();
        _locks.Clear();
        _lastCleanup = DateTime.UtcNow;
    }

    public int ProcessedCount => _processedCallIds.Count;

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        private bool _disposed;

        public Releaser(SemaphoreSlim sem) => _sem = sem;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sem.Release();
        }
    }
}
```

- [ ] **Step 2: Проверить сборку**

Run: `dotnet build -c Release`
Expected: 0 Error(s). Новый файл компилируется, ничего его пока не использует.

- [ ] **Step 3: Commit**

```bash
git add RingCentral_amoCRM/Helpers/CallProcessingGuard.cs
git commit -m "feat: CallProcessingGuard — shared call-id lock and processed set

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 2: `CallLogPollingService` переключается на `CallProcessingGuard`

Чистый рефакторинг: убираем приватный `HashSet<string> _processedCallIds`
и `_lastProcessedCleanup`, используем инжектированный `CallProcessingGuard`.
Поведение не меняется.

**Files:**
- Modify: `RingCentral_amoCRM/Helpers/CallLogPollingService.cs`
- Modify: `RingCentral_amoCRM/Program.cs:67-70` (регистрация singleton;
  делаем здесь же, чтобы DI не сломался между шагами одного таска)

**Interfaces:**
- Consumes: `CallProcessingGuard` (Task 1) — `IsProcessed`, `MarkProcessed`,
  `ClearOlderThanIfDue`.
- Produces: `CallLogPollingService` теперь принимает `CallProcessingGuard`
  в конструкторе — этим же полем будет пользоваться Task 5.

- [ ] **Step 1: Зарегистрировать `CallProcessingGuard` в DI**

В `RingCentral_amoCRM/Program.cs`, сразу после строки 67
(`builder.Services.AddSingleton<AmoCrmService>();`):

```csharp
builder.Services.AddSingleton<CallProcessingGuard>();
```

- [ ] **Step 2: Убрать приватное поле, добавить зависимость**

В `RingCentral_amoCRM/Helpers/CallLogPollingService.cs` заменить:

```csharp
private readonly HashSet<string> _processedCallIds = new();
private DateTime _lastProcessedCleanup = DateTime.UtcNow;
```

на:

```csharp
private readonly CallProcessingGuard _guard;
```

В конструкторе добавить параметр `CallProcessingGuard guard` и
присвоить `_guard = guard;`.

- [ ] **Step 3: Заменить использование в `ProcessCallLogsAsync`**

Заменить блок очистки:

```csharp
if ((DateTime.UtcNow - _lastProcessedCleanup).TotalHours >= 24)
{
    _logger.LogInformation($"Clearing processed call IDs cache ({_processedCallIds.Count} entries)");
    _processedCallIds.Clear();
    _lastProcessedCleanup = DateTime.UtcNow;
}
```

на:

```csharp
_guard.ClearOlderThanIfDue(TimeSpan.FromHours(24));
```

Заменить:

```csharp
if (_processedCallIds.Contains(record.id))
{
    _logger.LogInformation($"Call {record.id} already processed, skipping duplicate");
    continue;
}
```

на:

```csharp
if (_guard.IsProcessed(record.id))
{
    _logger.LogInformation($"Call {record.id} already processed, skipping duplicate");
    continue;
}
```

Заменить:

```csharp
_processedCallIds.Add(record.id);
```

на:

```csharp
_guard.MarkProcessed(record.id);
```

- [ ] **Step 4: Проверить сборку**

Run: `dotnet build -c Release`
Expected: 0 Error(s).

- [ ] **Step 5: Ручная проверка поведения не изменилась**

Прочитать получившийся `ProcessCallLogsAsync` целиком и сверить с
поведением до правки: очистка раз в 24 часа, pre-check перед обработкой,
пометка после успешного `ProcessCallRecordAsync` — логика идентична,
поменялось только где хранится состояние.

- [ ] **Step 6: Commit**

```bash
git add RingCentral_amoCRM/Helpers/CallLogPollingService.cs RingCentral_amoCRM/Program.cs
git commit -m "refactor: CallLogPollingService uses shared CallProcessingGuard

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 3: `AmoCrmService.ProcessSingleCallAsync` — единая обработка звонка

Переносим `ProcessCallRecordAsync` из `CallLogPollingService` в
`AmoCrmService`, оборачиваем в guard-lock, добавляем `created_at`
проброс. `CallLogPollingService` зовёт новый метод вместо своей копии.

**Files:**
- Modify: `RingCentral_amoCRM/Helpers/AmoCrmService.cs`
- Modify: `RingCentral_amoCRM/Helpers/CallLogPollingService.cs`

**Interfaces:**
- Consumes: `CallProcessingGuard` (Task 1), существующие
  `FindLeadByPhoneNumberAsync`, `ResolveTargetLeadAsync`, `NoteExistsAsync`,
  `UploadCallRecordingAsync`, `CreateCallNoteAsync` — все уже в
  `AmoCrmService`.
- Produces:
  ```csharp
  public enum CallProcessingResult
  {
      Attached,
      Duplicate,
      NoLead,
      NoNumber,
      Error
  }

  public async Task<CallProcessingResult> ProcessSingleCallAsync(
      RingCentral.CallLogRecord record,
      CallProcessingGuard guard,
      string logPrefix,
      DateTime? callStartUtc = null)
  ```
  Task 5 (`LateAttachService`) и Task 6 (`CallLogPollingService`, уже
  обновлённый) оба зовут этот метод с разными `logPrefix` (`"POLL"` /
  `"LATE"`) для унифицированного построчного лога.

- [ ] **Step 1: Добавить `CallProcessingResult` и `ProcessSingleCallAsync` в `AmoCrmService`**

Добавить в начало файла `RingCentral_amoCRM/Helpers/AmoCrmService.cs`
(перед `public class AmoCrmService`):

```csharp
public enum CallProcessingResult
{
    Attached,
    Duplicate,
    NoLead,
    NoNumber,
    Error
}
```

Добавить метод в `AmoCrmService` (после `CreateCallNoteAsync`, конец класса):

```csharp
// Единая точка обработки одного звонка. Вызывается и основным поллингом
// (record только что увиден, свежий), и поздней привязкой (record может
// быть до LateAttach:LookbackDays суток "старым"). logPrefix уходит в
// каждую лог-строку, чтобы различать источник в общем логе.
public async Task<CallProcessingResult> ProcessSingleCallAsync(
    RingCentral.CallLogRecord record,
    CallProcessingGuard guard,
    string logPrefix,
    DateTime? callStartUtc = null)
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

        if (await NoteExistsAsync(targetLeadId.Value, noteType, record.id))
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
```

`MissedCallResults` сейчас `private static readonly` в
`CallLogPollingService` — перенести это поле в `AmoCrmService` (рядом с
константами `ClosedWonStatusId`/`ClosedLostStatusId`):

```csharp
private static readonly HashSet<string> MissedCallResults = new(StringComparer.OrdinalIgnoreCase)
{
    "Missed", "No Answer", "Voicemail", "Rejected", "Busy", "Abandoned"
};
```

- [ ] **Step 2: Обновить сигнатуру `CreateCallNoteAsync` под `callStartUtc`**

Это задел под Task 4 (там же реализуем сам `created_at`) — здесь только
добавляем параметр, чтобы сигнатура совпала с вызовом выше:

В `RingCentral_amoCRM/Helpers/AmoCrmService.cs` заменить сигнатуру:

```csharp
public async Task CreateCallNoteAsync(long leadId, RingCentral.CallLogRecord record, string recURL, string customerPhoneNumber, bool isMissed)
```

на:

```csharp
public async Task CreateCallNoteAsync(long leadId, RingCentral.CallLogRecord record, string recURL, string customerPhoneNumber, bool isMissed, DateTime? callStartUtc = null)
```

Тело метода пока не меняем — параметр используется в Task 4.

- [ ] **Step 3: Удалить дублирующую логику из `CallLogPollingService`, вызвать общий метод**

В `RingCentral_amoCRM/Helpers/CallLogPollingService.cs` удалить весь
метод `ProcessCallRecordAsync` (строки 256-321) и поле
`MissedCallResults` (строки 29-32, теперь живёт в `AmoCrmService`).

В `ProcessCallLogsAsync`, заменить:

```csharp
_logger.LogInformation($"Start processing call {record.id} call completed at {record.startTime}");
await ProcessCallRecordAsync(record);

_guard.MarkProcessed(record.id);
```

на:

```csharp
_logger.LogInformation($"Start processing call {record.id} call completed at {record.startTime}");
await _amoService.ProcessSingleCallAsync(record, _guard, "POLL");
```

(`MarkProcessed` теперь вызывается внутри `ProcessSingleCallAsync` после
успешного создания заметки/обнаружения дубля — убираем внешний вызов,
чтобы не помечать звонок как обработанный при `NoLead`/`NoNumber`/`Error`,
что и требуется: такие звонки должны быть видны следующему циклу
поздней привязки.)

**Важно:** это меняет поведение относительно нынешнего кода — раньше
`_processedCallIds.Add(record.id)` вызывался всегда после
`ProcessCallRecordAsync`, включая случаи "no lead". Теперь `NoLead`
звонки не попадают в guard как processed, что и нужно: без этого
поздняя привязка не смогла бы их подобрать (guard сказал бы "уже
обработан"). Дубли (`Duplicate`) и успешные (`Attached`) по-прежнему
помечаются.

- [ ] **Step 4: Проверить сборку**

Run: `dotnet build -c Release`
Expected: 0 Error(s).

- [ ] **Step 5: Ручная проверка**

Прочитать итоговый `AmoCrmService.ProcessSingleCallAsync` и
`CallLogPollingService.ProcessCallLogsAsync` целиком, сверить с
ProcessCallRecordAsync до переноса — шаги (search number → find leads →
resolve lead → note exists → upload recording → create note →
логирование) должны совпадать 1:1, плюс guard lock вокруг блока
"проверка+создание".

- [ ] **Step 6: Commit**

```bash
git add RingCentral_amoCRM/Helpers/AmoCrmService.cs RingCentral_amoCRM/Helpers/CallLogPollingService.cs
git commit -m "refactor: move call-record processing into AmoCrmService.ProcessSingleCallAsync

Shared by CallLogPollingService and the upcoming LateAttachService.
NoLead/NoNumber/Error calls no longer mark the guard as processed, so
late attach can still pick them up later.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 4: `created_at` в `CreateCallNoteAsync`

**Files:**
- Modify: `RingCentral_amoCRM/Helpers/AmoCrmService.cs`

**Interfaces:**
- Consumes: сигнатура `CreateCallNoteAsync(..., DateTime? callStartUtc = null)`
  уже добавлена в Task 3, Step 2.
- Produces: заметка в amoCRM с полем `created_at` (unix timestamp), если
  `callStartUtc` передан.

- [ ] **Step 1: Реализовать `created_at` в теле заметки**

В `RingCentral_amoCRM/Helpers/AmoCrmService.cs`, внутри
`CreateCallNoteAsync`, после блока формирования `source`/`isMissed`
(строки ~617-621) и перед построением `rootJsonArray`, добавить:

```csharp
// Человекочитаемая метка времени звонка в тексте — страховка на случай,
// если amoCRM скорректирует created_at для очень старых дат (типично
// для звонков, найденных поздней привязкой, возрастом до LookbackDays).
if (callStartUtc.HasValue)
{
    source = $"{source} [звонок {callStartUtc.Value:dd.MM.yyyy HH:mm} UTC]";
}
```

Изменить построение `rootJsonArray`, добавив `created_at` в корневой
объект заметки (рядом с `note_type`/`params`, не внутрь `params`):

```csharp
var noteObject = new JsonObject
{
    ["note_type"] = record.direction == "Inbound" ? "call_in" : "call_out",
    ["params"] = paramsObject
};

if (callStartUtc.HasValue)
{
    noteObject["created_at"] = ((DateTimeOffset)DateTime.SpecifyKind(callStartUtc.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}

var rootJsonArray = new JsonArray { noteObject };
```

(это заменяет существующий литерал `rootJsonArray` в методе — сравнить
с текущим кодом строк 639-646 и убрать старую инициализацию).

- [ ] **Step 2: Проверить сборку**

Run: `dotnet build -c Release`
Expected: 0 Error(s).

- [ ] **Step 3: Ручная проверка сериализации**

Временно добавить в `Program.cs` (после `app.Run();` не дойдёт — вместо
этого использовать `dotnet-script`-стиль недоступен) — проще: добавить
временный `[HttpGet]`-эндпоинт не нужно (нарушает "без новых публичных
эндпоинтов"). Вместо этого: прочитать собранный `noteObject.ToJsonString()`
глазами по коду — `created_at` как число (unix timestamp), не строка.
Убедиться, что `System.Text.Json.Nodes.JsonObject` сериализует `long` в
JSON-число без кавычек (стандартное поведение `JsonValue.Create(long)`,
используемое неявно при `noteObject["created_at"] = <long>`).

- [ ] **Step 4: Commit**

```bash
git add RingCentral_amoCRM/Helpers/AmoCrmService.cs
git commit -m "feat: set created_at on call notes to actual call time

amoCRM v4 accepts created_at (unix timestamp) at note creation. Falls
back to default (now) when callStartUtc is not provided — no behavior
change for calls found by the main poller today.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 5: Модели ответов amoCRM для списков контактов/сделок по `updated_at`

**Files:**
- Modify: `RingCentral_amoCRM/Models/AmoContactModel.cs`
- Modify: `RingCentral_amoCRM/Models/AmoLeadModel.cs`

**Interfaces:**
- Consumes: ничего нового.
- Produces:
  - `AmoContactModel.cs`: `CustomFieldsValue` получает `FieldCode` (уже
    есть) — используется в Task 6 для фильтрации по `"PHONE"`.
    `AmoCrmContact` получает `UpdatedAt` (long, unix timestamp).
  - `AmoLeadModel.cs`: `AmoCrmLeadDetail` получает `Embedded` со
    списком контактов (`ContactsEmbedded`/`AmoCrmLeadContactRef`), чтобы
    ответ `GET /leads?with=contacts` парсился.

- [ ] **Step 1: Добавить `UpdatedAt` в `AmoCrmContact`**

В `RingCentral_amoCRM/Models/AmoContactModel.cs`, в класс `AmoCrmContact`
добавить:

```csharp
[JsonPropertyName("updated_at")]
public long UpdatedAt { get; set; }
```

- [ ] **Step 2: Добавить `Embedded` (контакты) в `AmoCrmLeadDetail`**

В `RingCentral_amoCRM/Models/AmoLeadModel.cs` добавить в
`AmoCrmLeadDetail`:

```csharp
[JsonPropertyName("_embedded")]
public LeadContactsEmbedded Embedded { get; set; }
```

И новый класс в том же файле:

```csharp
public class LeadContactsEmbedded
{
    [JsonPropertyName("contacts")]
    public List<LeadContactRef> Contacts { get; set; }
}

public class LeadContactRef
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
}
```

- [ ] **Step 3: Проверить сборку**

Run: `dotnet build -c Release`
Expected: 0 Error(s).

- [ ] **Step 4: Commit**

```bash
git add RingCentral_amoCRM/Models/AmoContactModel.cs RingCentral_amoCRM/Models/AmoLeadModel.cs
git commit -m "feat: model fields for contacts/leads updated_at polling

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 6: `AmoCrmService` — методы опроса изменённых контактов и сделок

**Files:**
- Modify: `RingCentral_amoCRM/Helpers/AmoCrmService.cs`

**Interfaces:**
- Consumes: модели из Task 5 (`AmoCrmContact.UpdatedAt`,
  `AmoCrmLeadDetail.Embedded.Contacts`), существующий
  `AmoCrmContactsResponse`/`AmoCrmLeadsListResponse` (пагинация через
  `_page`, но amoCRM v4 в реальности пагинируется через `page=`
  query-параметр и отсутствие `_embedded` на последней странице — тот
  же паттерн, что уже используется в `NoteExistsAsync`).
- Produces:
  ```csharp
  public async Task<List<AmoCrmContact>> GetUpdatedContactsAsync(DateTime sinceUtc)
  public async Task<List<AmoCrmLeadDetail>> GetUpdatedLeadsWithContactsAsync(DateTime sinceUtc)
  public static HashSet<string> ExtractNormalizedPhones(IEnumerable<AmoCrmContact> contacts)
  ```
  Task 7 (`LateAttachService`) вызывает эти три метода напрямую.

- [ ] **Step 1: Добавить константы пагинации**

В `AmoCrmService`, рядом с `NoteExistsMaxPages`:

```csharp
private const int UpdatedEntitiesPageSize = 250;
private const int UpdatedEntitiesMaxPages = 50;
```

- [ ] **Step 2: Реализовать `GetUpdatedContactsAsync`**

```csharp
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
```

- [ ] **Step 3: Реализовать `GetUpdatedLeadsWithContactsAsync`**

```csharp
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
```

**Примечание для исполнителя:** оба метода намеренно **бросают
исключение** при ошибке HTTP (в отличие от `NoteExistsAsync`, который
fail-open). Это осознанное решение: ошибка на этапе сбора изменений
должна прервать весь цикл `LateAttachService` и НЕ сдвигать точку
отсчёта (см. Task 7) — если проглотить ошибку здесь, `LateAttachService`
решит, что изменений просто не было, и молча продвинет
`_lastCycleStartUtc`, потеряв те самые изменения.

- [ ] **Step 4: Реализовать `ExtractNormalizedPhones`**

Добавить статический метод-хелпер нормализации телефона (последние 10
цифр — тот же принцип, что использует `FindLeadByPhoneNumberAsync` для
поиска, но там для query, здесь — для сравнения множеств):

```csharp
// Последние 10 цифр — минимальный общий знаменатель между форматами
// amoCRM (+1XXXXXXXXXX/XXXXXXXXXX) и RingCentral (phoneNumber в call log).
private static string NormalizePhone(string phone)
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
```

- [ ] **Step 5: Проверить сборку**

Run: `dotnet build -c Release`
Expected: 0 Error(s).

- [ ] **Step 6: Ручная проверка `NormalizePhone`/`ExtractNormalizedPhones`**

Временно добавить в конец `Program.cs`, **перед** `app.Run();`, блок
для ручной проверки (удалить после проверки, до коммита):

```csharp
var testContacts = new List<AmoCrmContact>
{
    new AmoCrmContact
    {
        CustomFieldsValues = new List<CustomFieldsValue>
        {
            new CustomFieldsValue
            {
                FieldCode = "PHONE",
                Values = new List<CustomFieldValueItem>
                {
                    new CustomFieldValueItem { Value = "+1 (954) 555-1234" },
                    new CustomFieldValueItem { Value = "9545559999" }
                }
            }
        }
    }
};
var testPhones = AmoCrmService.ExtractNormalizedPhones(testContacts);
Console.WriteLine("TEST PHONES: " + string.Join(",", testPhones));
// Ожидается: 9545551234,9545559999
```

Run: `dotnet run --project RingCentral_amoCRM` (прервать после вывода
строки `TEST PHONES:` в консоль — сервис попытается дальше
авторизоваться в RC/amoCRM и может упасть без реальных кредов, это
ожидаемо и не мешает проверке).
Expected: в консоли строка `TEST PHONES: 9545551234,9545559999`.

Удалить временный блок из `Program.cs` после проверки.

- [ ] **Step 7: Commit**

```bash
git add RingCentral_amoCRM/Helpers/AmoCrmService.cs
git commit -m "feat: AmoCrmService methods to poll updated contacts/leads and extract phones

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 7: `LateAttachService`

**Files:**
- Create: `RingCentral_amoCRM/Helpers/LateAttachService.cs`
- Modify: `RingCentral_amoCRM/Program.cs`

**Interfaces:**
- Consumes:
  - `AmoCrmService.GetUpdatedContactsAsync(DateTime)` (Task 6)
  - `AmoCrmService.GetUpdatedLeadsWithContactsAsync(DateTime)` (Task 6)
  - `AmoCrmService.ExtractNormalizedPhones(IEnumerable<AmoCrmContact>)` (Task 6)
  - `AmoCrmService.ProcessSingleCallAsync(...)` (Task 3)
  - `CallProcessingGuard` (Task 1)
  - `RestClient` (существующий singleton, тот же что в
    `CallLogPollingService`) для `CallLog().List(...)`
- Produces: `LateAttachService : BackgroundService`, зарегистрирован в
  DI как hosted service. Ничего дальше по плану от него не зависит —
  последняя задача основной цепочки.

- [ ] **Step 1: Написать `LateAttachService`**

```csharp
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
                await RunCycleAsync();
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

    private async Task RunCycleAsync()
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

            _logger.LogInformation("LATE id={Id} number={Number} age_h={AgeH:F1}", record.id, record.to?.phoneNumber ?? record.from?.phoneNumber, ageHours);

            CallProcessingResult result;
            try
            {
                result = await _amoService.ProcessSingleCallAsync(record, _guard, "LATE", callStartUtc);
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
        }

        sw.Stop();
        _logger.LogInformation(
            "LATE cycle contacts={Contacts} leads={Leads} phones={Phones} calls_matched={Matched} attached={Attached} dup={Dup} errors={Errors} duration={Duration:F1}",
            contactsCount, leadsCount, phonesCount, matchedCount, attached, dup, errors, sw.Elapsed.TotalSeconds);

        // Точка отсчёта продвигается только когда весь цикл дошёл до конца.
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

        var normalized = NormalizePhone(searchNumber);
        return normalized != null && phones.Contains(normalized);
    }

    private static string NormalizePhone(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length >= 10 ? digits[^10..] : null;
    }

    private static DateTime ParseStartUtc(string startTime)
    {
        return DateTime.Parse(startTime, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal);
    }
}
```

**Замечание по дублированию `NormalizePhone`:** `AmoCrmService` (Task 6)
и `LateAttachService` оба содержат приватный `NormalizePhone` с
идентичной логикой (последние 10 цифр). Дублирование двух похожих
приватных статических методов в разных файлах — не значит "не
переиспользуем существующую логику" (spec запрещает дублировать именно
*обработку звонка*, т.е. `ResolveTargetLeadAsync`/`NoteExistsAsync`/
`CreateCallNoteAsync`, что учтено через `ProcessSingleCallAsync`).
Здесь это две copies тривиальной 3-строчной нормализации, что приемлемо
и не требует общего хелпер-класса — но если предпочтительнее избежать
дублирования, сделать `AmoCrmService.NormalizePhone` внутренним
`internal static` и переиспользовать напрямую вместо копии в
`LateAttachService` (эквивалентная правка, на усмотрение исполнителя).

- [ ] **Step 2: Добавить `GetContactsByIdsAsync` в `AmoCrmService`**

`LateAttachService` выше вызывает `_amoService.GetContactsByIdsAsync(ids)`,
которого ещё нет. Добавить в `RingCentral_amoCRM/Helpers/AmoCrmService.cs`,
рядом с `GetUpdatedContactsAsync`:

```csharp
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
```

Так же, как `GetUpdatedContactsAsync`/`GetUpdatedLeadsWithContactsAsync`,
этот метод бросает исключение при ошибке — по той же причине (см.
Task 6 примечание): сбой здесь должен прервать весь цикл
`LateAttachService.RunCycleAsync`, а не молча продолжить с неполным
набором телефонов.

- [ ] **Step 3: Зарегистрировать `LateAttachService` в DI**

В `RingCentral_amoCRM/Program.cs`, после
`builder.Services.AddHostedService<CallLogPollingService>();`:

```csharp
builder.Services.AddHostedService<LateAttachService>();
```

- [ ] **Step 4: Проверить сборку**

Run: `dotnet build -c Release`
Expected: 0 Error(s).

- [ ] **Step 5: Ручная проверка запуска**

Run: `dotnet run --project RingCentral_amoCRM` (с реальными или тестовыми
кредами в `appsettings.json`, локально — не на проде).
Expected: в логах при старте строка
`LATE settings: enabled=True intervalMin=15 lookbackDays=14 startupLookbackH=24`.
Остановить процесс после проверки строки (Ctrl+C).

- [ ] **Step 6: Ручная проверка `Enabled=false`**

Run:
```bash
LateAttach__Enabled=false dotnet run --project RingCentral_amoCRM
```
(PowerShell: `$env:LateAttach__Enabled="false"; dotnet run --project RingCentral_amoCRM`)

Expected: в логах строка `LATE settings: enabled=False ...`, затем
`LateAttachService disabled via LateAttach:Enabled=false, not starting.`
и никаких `LATE cycle` строк после. Остановить процесс, сбросить
переменную окружения (`Remove-Item Env:LateAttach__Enabled` в PowerShell
/ `unset LateAttach__Enabled` в bash).

- [ ] **Step 7: Commit**

```bash
git add RingCentral_amoCRM/Helpers/LateAttachService.cs RingCentral_amoCRM/Helpers/AmoCrmService.cs RingCentral_amoCRM/Program.cs
git commit -m "feat: LateAttachService — poll amoCRM changes, match RC call log, attach

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 8: Догоняющий проход при старте в `CallLogPollingService`

**Files:**
- Modify: `RingCentral_amoCRM/Helpers/CallLogPollingService.cs`

**Interfaces:**
- Consumes: `IConfiguration` (уже есть в конструкторе),
  `_amoService.ProcessSingleCallAsync` (Task 3), `_guard` (Task 2).
- Produces: ничего нового для других задач — конечная задача основной
  цепочки функциональности.

- [ ] **Step 1: Прочитать конфигурацию `LateAttach` в конструкторе**

В `RingCentral_amoCRM/Helpers/CallLogPollingService.cs` добавить поля:

```csharp
private readonly bool _lateAttachEnabled;
private readonly int _startupLookbackHours;
```

В конструктор, после чтения `_jwt`/`_expiresAt`, добавить:

```csharp
_lateAttachEnabled = configuration.GetValue("LateAttach:Enabled", true);
_startupLookbackHours = configuration.GetValue("LateAttach:StartupLookbackHours", 24);
```

- [ ] **Step 2: Добавить догоняющий проход в `ExecuteAsync`**

В `RingCentral_amoCRM/Helpers/CallLogPollingService.cs`, метод
`ExecuteAsync`, после существующего `await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);`
и до `while (!stoppingToken.IsCancellationRequested)`, добавить:

```csharp
if (_lateAttachEnabled)
{
    try
    {
        if (_amoService.IsExpired())
        {
            await _amoService.InitializeAsync();
        }
        await EnsureAuthorized();
        await RunStartupCatchUpAsync();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Startup catch-up pass failed; continuing with regular polling.");
    }
}
```

Добавить новый метод (рядом с `ProcessCallLogsAsync`):

```csharp
// Однократный догоняющий проход при старте: основной опрос смотрит
// только ~5 минут назад, поэтому звонки существующим контактам за время
// простоя (рестарт/деплой) иначе теряются — поздняя привязка их не
// подберёт, т.к. эти контакты не менялись. Идемпотентность через uniq
// (NoteExistsAsync/guard) делает повторный проход безопасным.
private async Task RunStartupCatchUpAsync()
{
    var dateFrom = DateTime.UtcNow.AddHours(-_startupLookbackHours);
    _logger.LogInformation("Startup catch-up: fetching calls since {DateFrom}", dateFrom);

    int totalFetched = 0;
    for (int page = 1; page <= CallLogMaxPages; page++)
    {
        var callLogParameters = new ReadCompanyCallLogParameters()
        {
            perPage = CallLogPageSize,
            page = page,
            view = "Detailed",
            withRecording = true,
            dateFrom = dateFrom.ToString("o"),
        };
        var callLogs = await _rc.Restapi().Account().CallLog().List(callLogParameters);

        if (callLogs?.records == null || callLogs.records.Length == 0)
        {
            break;
        }

        totalFetched += callLogs.records.Length;

        foreach (var record in callLogs.records)
        {
            try
            {
                if (record.from?.extensionId != null && record.to?.extensionId != null)
                {
                    continue;
                }

                await _amoService.ProcessSingleCallAsync(record, _guard, "STARTUP");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup catch-up: error processing call {RecordId}", record.id);
            }
        }

        if (callLogs.records.Length < CallLogPageSize)
        {
            break;
        }
    }

    _logger.LogInformation("Startup catch-up complete: {Total} calls scanned", totalFetched);
}
```

- [ ] **Step 3: Проверить сборку**

Run: `dotnet build -c Release`
Expected: 0 Error(s).

- [ ] **Step 4: Ручная проверка**

Run: `dotnet run --project RingCentral_amoCRM` локально с тестовыми
кредами.
Expected: в логах при старте строка `Startup catch-up: fetching calls
since <дата −24ч>`, затем `Startup catch-up complete: N calls scanned`,
**до** первой строки обычного цикла (`🚀 CallLogPollingService
starting...` уже логируется раньше — проверить порядок: старая строка
идёт первой, затем catch-up, затем обычный while-цикл). Остановить
процесс.

Run с `LateAttach__Enabled=false`: в логах строки `Startup catch-up`
отсутствуют, сразу обычный цикл.

- [ ] **Step 5: Commit**

```bash
git add RingCentral_amoCRM/Helpers/CallLogPollingService.cs
git commit -m "feat: one-time startup catch-up pass in CallLogPollingService

Covers calls to already-existing contacts during downtime, which late
attach cannot find (those contacts weren't modified). Gated by
LateAttach:Enabled — the kill switch disables both.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 9: Финальная проверка сборки и самопроверка по чек-листу задачи

**Files:** нет изменений — только верификация.

- [ ] **Step 1: Полная пересборка с нуля**

Run: `dotnet clean RingCentral_amoCRM/RingCentral_amoCRM.csproj && dotnet build -c Release`
Expected: 0 Error(s), 0 Warning(s) относящихся к новому коду (существующие
предупреждения, если были до этой ветки, не в счёт).

- [ ] **Step 2: Grep на секреты перед финальным коммитом (правило CLAUDE.md)**

Run: `grep -rnE "eyJ[A-Za-z0-9_-]{30,}|def50[0-9a-f]{30,}" RingCentral_amoCRM --include=*.cs`
Expected: пусто (no matches).

- [ ] **Step 3: Ручной end-to-end прогон по плану проверки из спеки**

Выполнить план из `DOCS/superpowers/specs/2026-09-21-late-call-attach-design.md`,
раздел "План проверки на тестовом контакте", на тестовом амоCRM-аккаунте
и тестовом RC-номере (не на проде):

1. Позвонить с номера, которого нет в CRM → в логах `LATE ...
   action=no_lead` (или `POLL ... action=no_lead` при обычном опросе).
2. Завести контакт с этим номером и сделку в amoCRM.
3. Подождать ≤ `IntervalMinutes` → в логах `LATE id=... action=attached`,
   заметка на сделке с `created_at` = время звонка.
4. Перезапустить процесс → следующий цикл не дублирует заметку
   (`action=dup` в логах или тишина, если `NoteExistsAsync` отработал
   раньше guard).
5. Откатить на предыдущий коммит/ветку — проверить, что основной
   поллинг работает как раньше (без поздней привязки).
6. `LateAttach__Enabled=false` + рестарт → убедиться, что ни поздняя
   привязка, ни startup catch-up не запускаются, обычный поллинг работает.

- [ ] **Step 4: Не коммитить — эта задача только проверочная**

Если в ходе проверки найдены расхождения с ожидаемым поведением —
завести отдельные исправляющие коммиты по месту (не переписывать историю
предыдущих задач).

---

## Self-Review Notes

- **Spec coverage:** все разделы спеки покрыты — RC-запрос за период
  (Task 7, `FetchCallLogAsync`), guard (Task 1-3), `created_at` (Task 4),
  наблюдаемость (Task 7 лог-строки `LATE settings`/`LATE cycle`/`LATE id`,
  Task 8 `STARTUP`-префикс для стартового прохода — не указан в спеке
  явно как отдельный формат, но переиспользует `ProcessSingleCallAsync`
  и, следовательно, ту же построчную схему `{Prefix} id=... action=...`),
  конфигурация и kill switch (Task 7 Step 1, Task 8 Step 1), контакт без
  сделки → со сделкой (Task 6 `GetUpdatedLeadsWithContactsAsync` +
  `GetContactsByIdsAsync`), ошибки посреди цикла не двигают точку
  отсчёта (Task 7 `RunCycleAsync`, явные `return` без обновления
  `_lastCycleStartUtc` на каждом catch).
- **Гонка:** явно закрыта в Task 1 (`AcquireAsync`) и используется в
  Task 3 (`ProcessSingleCallAsync`) — единственная точка создания
  заметки для обоих сервисов.
- **Типы:** `CallProcessingResult` определён в Task 3 и используется
  без изменений в Task 7; `CreateCallNoteAsync` сигнатура согласована
  между Task 3 (добавление параметра) и Task 4 (использование).
- **Не в плане:** тестовый проект — сознательно, в кодовой базе его нет,
  проверка идёт через сборку + ручные прогоны на каждом шаге (см. Global
  Constraints).
