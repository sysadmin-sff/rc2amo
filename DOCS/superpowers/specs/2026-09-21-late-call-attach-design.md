# Поздняя привязка звонков — дизайн

Дата: 2026-09-21
Ветка: `fix/idempotent-notes-and-lead-matching` (feature-ветка от неё)

## Проблема

Звонок, для которого в момент обработки (`CallLogPollingService`) в amoCRM
не нашлось контакта/сделки по номеру телефона, сейчас теряется навсегда:
`FindLeadByPhoneNumberAsync` возвращает `null`, обработка логирует
"no lead" и выходит. Если контакт со сделкой появится в amoCRM позже
(например, менеджер вручную завёл лид после звонка), звонок к нему уже
не привяжется.

## Требования

- Без своей БД, без вебхуков, без новых публичных эндпоинтов.
- Не менять поведение для звонков, которые находятся сразу.
- Не трогать `SubscriptionService`, формат `appsettings.json`, парсинг времени.
- SMS — не в этом этапе.
- Живой `appsettings.json` на проде перезаписывается приложением — новые
  ключи туда не добавлять, конфигурация только через переменные окружения
  с дефолтами в коде.
- `LateAttach:Enabled=false` — аварийный выключатель, полностью отключает
  и позднюю привязку, и догоняющий проход при старте.

## Подход: опрос изменений в amoCRM

Отдельный `BackgroundService`, раз в `LateAttach:IntervalMinutes` (default 15):

1. Запросить в amoCRM контакты, изменённые с прошлого цикла
   (`filter[updated_at][from]`), и сделки, изменённые с прошлого цикла
   вместе с привязанными контактами (`with=contacts`). Сделки нужны
   отдельно от контактов: если к существующему (не изменившемуся) контакту
   добавили сделку, сам контакт может не попасть в выборку по `updated_at`.
   Пагинация обязательна (лимит amoCRM v4 — 250 записей/страница).
2. Собрать телефоны этих контактов (поле `PHONE`, multitext — **все**
   значения, не только первое), нормализовать до последних 10 цифр.
3. Если телефонов нет — цикл закончен, в RingCentral не ходить.
4. Иначе — один запрос журнала звонков RC за `LateAttach:LookbackDays`
   (default 14), пагинация, фильтрация записей по совпадению нормализованного
   номера клиента (`searchNumber`, та же логика выбора from/to, что в
   основной обработке) с одним из телефонов — **локально**, не через RC.
5. Каждый найденный звонок — через существующую логику обработки одного
   звонка (переиспользуется, не дублируется).
6. Запомнить время начала цикла как точку отсчёта для следующего — но
   только если цикл завершился без фатальной ошибки.

### Почему один запрос за период, а не `phoneNumber` на каждый номер

RC Call Log — heavy-group эндпоинт с довольно жёстким rate limit
(десятки запросов/мин). Число изменённых контактов за цикл ничем не
ограничено (массовый импорт — сотни за раз), значит запрос на каждый
номер масштabируется линейно с N и:

- при импорте 500 контактов дал бы до 500 запросов к heavy-group
  эндпоинту за один цикл, упираясь в лимит и растягивая цикл на
  много минут;
- конкурировал бы за тот же лимит с основным `CallLogPollingService`,
  который опрашивает тот же Call Log каждые 5 минут.

Один запрос за `LookbackDays` с локальной фильтрацией не зависит от
числа контактов вообще — при импорте 500 контактов это тот же 1
(изредка 2-3, если записей за 14 дней много) запрос, что и в обычном
цикле. Компромисс — можем скачать больше записей, чем нужно, но это
дешевле, чем множить round-trips к heavy-group API.

### Запросов за цикл — оценка

**Обычный режим (мало изменений):**
- amoCRM: 1-2 запроса на контакты + 1-2 на сделки (пагинация) ≈ 2-4.
- RC: 1-3 запроса на Call Log (зависит от объёма звонков за 14 дней).
- Итого: ~5-7 запросов раз в 15 минут.

**Импорт 500 контактов:**
- amoCRM: контакты пагинируются по 250/страница → 2+ страниц контактов,
  аналогично для сделок — единицы дополнительных запросов, не сотни.
- RC: без изменений, всё тот же 1 (редко 2-3) запрос — не зависит от N.

### Обработка ошибок посреди цикла

Точка отсчёта (`_lastCycleStartUtc`, in-memory) обновляется **только**
в конце успешно завершённого цикла. Если запрос к amoCRM (контакты/сделки)
или к RC (Call Log) в середине цикла падает — цикл прерывается, ошибка
логируется (`errors` в итоговой строке), `_lastCycleStartUtc` не трогается.
Следующий цикл повторит тот же `filter[updated_at][from]`, так что
изменения не теряются. Идемпотентность через `uniq` делает повторный
проход безопасным.

Ошибка на уровне одного звонка (например, `CreateCallNoteAsync` вернул
ошибку) не должна прерывать весь цикл: ловится per-record, логируется
как `action=error`, обработка остальных звонков продолжается. Такой
звонок не попадёт в `CallProcessingGuard` как processed и будет
повторно рассмотрен на следующем цикле (или основным поллингом, если
попадёт в его окно).

### Точка отсчёта после рестарта

`_lastCycleStartUtc` живёт только в памяти. На первом цикле после
старта процесса — `now − LateAttach:StartupLookbackHours` (default 24).
Идемпотентность через `uniq` делает повторную обработку безопасной
при частых рестартах.

### Контакт без сделки — до и после

**Сейчас (без сделки):** `FindLeadByPhoneNumberAsync` не находит
`Embedded.Leads` у контакта → возвращает `null` → звонок логируется
как "no lead" и теряется безвозвратно. Это и есть баг, который чинит
эта фича.

**После добавления сделки:** сама сделка (только что создана/обновлена)
попадает в выборку `GetUpdatedLeadsWithContactsAsync` по своему
`updated_at`. Через `with=contacts` берём телефон привязанного контакта,
находим совпадение в журнале RC за `LookbackDays`, прогоняем через общую
обработку звонка — теперь она найдёт лид и создаст заметку.

## Простой сервиса при старте/деплое

Основной `CallLogPollingService` смотрит только ~15 минут назад
(`_recentCallWindow` + запас). Во время простоя (рестарт/деплой) звонки
**существующим** контактам теряются, а поздняя привязка их не подберёт —
эти контакты не менялись, значит не попадут в `filter[updated_at][from]`.

Добавляется однократный догоняющий проход в `CallLogPollingService` при
старте: `dateFrom = now − LateAttach:StartupLookbackHours`, дальше —
обычный режим (`_recentCallWindow`). Проход выполняется только если
`LateAttach:Enabled=true`; иначе пропускается (аварийный выключатель
отключает и это).

## Защита от гонки

Основной поллинг и поздняя привязка могут одновременно взять один и тот
же звонок (оба видят "заметки нет" в момент проверки и оба создают).
Нужна общая точка синхронизации.

### `CallProcessingGuard` (новый singleton)

Заменяет текущий приватный `HashSet<string> _processedCallIds` в
`CallLogPollingService`. Регистрируется в DI как singleton, оба сервиса
получают его через конструктор.

```csharp
public class CallProcessingGuard
{
    private readonly ConcurrentDictionary<string, byte> _processedCallIds = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public bool IsProcessed(string callId);
    public void MarkProcessed(string callId);
    public Task<IDisposable> AcquireAsync(string callId); // per-callId lock, release on Dispose
    public void ClearOlderThan(TimeSpan age); // раз в сутки, как сейчас
}
```

Использование в общем методе обработки звонка:

```csharp
using (await guard.AcquireAsync(record.id))
{
    if (guard.IsProcessed(record.id)) return Duplicate;
    // ResolveTargetLeadAsync → NoteExistsAsync → CreateCallNoteAsync
    guard.MarkProcessed(record.id);
}
```

Весь блок "проверка NoteExistsAsync + создание заметки" выполняется под
локом по конкретному call ID — второй сервис, попытавшийся обработать
тот же звонок одновременно, дождётся лока, увидит `IsProcessed == true`
и выйдет как `dup`, не создав вторую заметку.

`_processedCallIds` остаётся быстрым in-memory pre-check (источник
истины при рестарте — `NoteExistsAsync` через amoCRM, как и сейчас),
просто теперь он общий и потокобезопасный вместо приватного `HashSet`.

## Переиспользование логики обработки звонка

`CallLogPollingService.ProcessCallRecordAsync` переносится в
`AmoCrmService` как `ProcessSingleCallAsync(CallLogRecord record,
CallProcessingGuard guard, string logPrefix, DateTime? callStartUtc = null)`,
возвращающий результат обработки (attached / duplicate / no_lead /
no_number / error) для унифицированного логирования. Внутри — та же
последовательность, что сейчас в `ProcessCallRecordAsync`:
поиск `searchNumber` → `FindLeadByPhoneNumberAsync` →
`ResolveTargetLeadAsync` → guard lock → `NoteExistsAsync` →
(опционально) `UploadCallRecordingAsync` → `CreateCallNoteAsync`.

И `CallLogPollingService`, и `LateAttachService` вызывают этот единственный
метод — логика не дублируется.

## `created_at` заметки

amoCRM v4 REST API принимает `created_at` (unix timestamp, UTC) в теле
запроса при создании заметки (`POST /api/v4/leads/{id}/notes`). Это
задокументированное и поддерживаемое поведение.

`CreateCallNoteAsync` получает новый опциональный параметр
`DateTime? callStartUtc`. Если передан — в корневой объект заметки
(рядом с `note_type`/`params`) добавляется
`["created_at"] = ((DateTimeOffset)callStartUtc.Value).ToUnixTimeSeconds()`.
Текст заметки (`source`) дополнительно содержит время звонка
человекочитаемо — на случай, если amoCRM всё же скорректирует
`created_at` для очень старых дат (страховка, не полагаемся только на
поле).

Для звонков из основного потока (`CallLogPollingService`, находятся
сразу) поведение не меняется по сути: `callStartUtc` можно передавать
всегда (`record.startTime`, распарсенный как UTC — используем уже
существующий парсинг), эффект на "свежих" звонках нулевой (заметка и
так создаётся почти сразу после звонка).

## Запись разговора

Без изменений в логике: `UploadCallRecordingAsync` уже устроен так, что
при отсутствии `record.recording?.id` или ошибке загрузки возвращает
`null`, а `CreateCallNoteAsync` создаёт заметку без `link`, не падая.
Для звонков возрастом до 14 дней RC обычно ещё отдаёт запись; если нет —
существующий fail-soft путь отрабатывает как есть.

## Новые методы в `AmoCrmService`

- `GetUpdatedContactsAsync(DateTime sinceUtc)` — пагинация по
  `GET /api/v4/contacts?filter[updated_at][from]=<unix>&with=leads`,
  возвращает контакты с их `CustomFieldsValues` (для PHONE) и
  `Embedded.Leads`.
- `GetUpdatedLeadsWithContactsAsync(DateTime sinceUtc)` — пагинация по
  `GET /api/v4/leads?filter[updated_at][from]=<unix>&with=contacts`,
  возвращает сделки с привязанными контактами.
- Обе — с лимитом страниц (защита от бесконечного цикла при аномалии
  API), как у существующих методов (`NoteExistsMaxPages`,
  `CallLogMaxPages`).

Потребуются небольшие дополнения моделей: `AmoCrmContact` уже содержит
`CustomFieldsValues`, добавить туда же `UpdatedAt`, а модели для ответа
списков контактов/сделок с пагинацией — по образцу существующих
`AmoCrmLeadsListResponse`.

## Наблюдаемость

- При старте `LateAttachService`:
  `LATE settings: enabled=<bool> intervalMin=<N> lookbackDays=<N> startupLookbackH=<N>`
- На цикл:
  `LATE cycle contacts=<N> leads=<N> phones=<N> calls_matched=<N> attached=<N> dup=<N> errors=<N> duration=<сек>`
- На каждый звонок:
  `LATE id=<id> number=<номер> age_h=<часов> lead=<id|none> action=<attached|dup|no_lead|error>`

## Конфигурация

Дефолты в коде, переопределение через переменные окружения ASP.NET Core
(`__` как разделитель секций), `appsettings.json` не трогаем:

```csharp
var enabled = configuration.GetValue("LateAttach:Enabled", true);
var intervalMinutes = configuration.GetValue("LateAttach:IntervalMinutes", 15);
var lookbackDays = configuration.GetValue("LateAttach:LookbackDays", 14);
var startupLookbackHours = configuration.GetValue("LateAttach:StartupLookbackHours", 24);
```

Переменные окружения: `LateAttach__Enabled`, `LateAttach__IntervalMinutes`,
`LateAttach__LookbackDays`, `LateAttach__StartupLookbackHours`.

`LateAttach:Enabled=false`:
- `LateAttachService.ExecuteAsync` немедленно возвращается (сервис
  зарегистрирован в DI, но no-op) — не опрашивает amoCRM/RC.
- `CallLogPollingService` пропускает догоняющий проход при старте.

## Изменяемые/новые файлы

- Новый: `Helpers/CallProcessingGuard.cs`
- Новый: `Helpers/LateAttachService.cs`
- Правки: `Helpers/AmoCrmService.cs` (новые методы + `ProcessSingleCallAsync`
  + `created_at` в `CreateCallNoteAsync`)
- Правки: `Helpers/CallLogPollingService.cs` (использует
  `CallProcessingGuard` вместо приватного `HashSet`, вызывает
  `AmoCrmService.ProcessSingleCallAsync`, догоняющий проход при старте)
- Правки: `Program.cs` (регистрация `CallProcessingGuard` как singleton,
  `AddHostedService<LateAttachService>`)
- Возможные небольшие дополнения моделей (`AmoContactModel.cs`,
  `AmoLeadModel.cs`) под ответы списков контактов/сделок с пагинацией
  и `UpdatedAt`.
- Не трогаем: `SubscriptionService.cs`, `SubscriptionHostedService.cs`,
  парсинг времени в `IsRecentCall`.

## План проверки на тестовом контакте

1. Позвонить с номера, которого нет в CRM → в логах `no_lead` /
   `action=no_lead`, звонок не потерян (останется в окне LookbackDays
   для поздней привязки).
2. Завести контакт с этим номером и сделку.
3. Подождать ≤ `IntervalMinutes` (15 мин по умолчанию) → в логах
   `LATE ... action=attached`, заметка появилась на сделке с корректным
   `created_at` = время звонка.
4. Рестарт сервиса → следующий цикл поздней привязки (или основной
   поллинг, если попадёт в окно) не должен задублировать заметку —
   проверить через `NoteExistsAsync`/лог `action=dup`.
5. Откат: переключить ветку/деплой на предыдущую версию — проверить,
   что основной поллинг продолжает работать как раньше.
6. Аварийное отключение: `LateAttach__Enabled=false` в окружении →
   рестарт → в логах видно `enabled=false`, догоняющий проход и цикл
   поздней привязки не выполняются, основной поллинг работает как
   раньше.

## Коммиты

Отдельные логические коммиты (примерно):
1. `CallProcessingGuard` + переключение `CallLogPollingService` на него.
2. Перенос `ProcessCallRecordAsync` → `AmoCrmService.ProcessSingleCallAsync`
   (чистый рефакторинг, без изменения поведения).
3. `created_at` в `CreateCallNoteAsync`.
4. Новые методы `AmoCrmService` для опроса изменённых контактов/сделок.
5. `LateAttachService` (новый сервис, конфигурация, регистрация в DI).
6. Догоняющий проход при старте в `CallLogPollingService`.

Каждый коммит — `dotnet build -c Release` без ошибок.
