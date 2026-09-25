# rc2amocrm — правила работы

Репозиторий ПУБЛИЧНЫЙ.
- Никогда не создавать и не коммитить appsettings.json / appsettings.*.json.
- Никаких токенов, JWT, client secret, паролей в коде, комментариях, тестах, логах.
  Для примеров конфигурации — только appsettings.example.json с пустыми значениями.
- Перед каждым коммитом: grep -rnE "eyJ[A-Za-z0-9_-]{30,}|def50[0-9a-f]{30,}" .

Прод:
- Сервис крутится на сервере под pm2 (dotnet RingCentral_amoCRM.dll, порт 5038,
  ASPNETCORE_PATHBASE=/rc2amocrm). Деплой делается вручную, не отсюда.
- Живой appsettings.json на сервере перезаписывается самим приложением
  (обновление токенов). Любая логика, меняющая формат этого файла, — только
  с явного согласия.
- Изменения — в отдельной ветке. Сборка `dotnet build -c Release` должна
  проходить без ошибок.

Контекст:
- Уже исправлено: парсинг startTime как UTC, TotalMinutes вместо Minutes,
  дедупликация по call ID в памяти (HashSet, сбрасывается при рестарте).

  Проверено на проде (amoCRM v4, screenfactory):
- call_in/call_out: params.uniq принимается; created_at при создании
  принимается и сохраняется как есть; updated_at = момент создания.
- call_in/call_out: params.source ОБЯЗАТЕЛЕН и не может быть пустым —
  400 params.source NotBlank/NotNullable. RC CallLog не гарантирует
  record.from.name/record.to.name (пусто для внешних абонентов без
  записи в адресной книге) — всегда нужен fallback (используется
  "RingCentral").
- call_in/call_out: params.duration ТОЖЕ ОБЯЗАТЕЛЕН — 400 FieldMissing
  без него. Как и params.source, ни в одном официальном источнике не
  заявлен обязательным — обязательность обоих полей выяснена только
  по факту 400 в проде. Для источников без естественной длительности
  разговора (голосовая почта) — передавать 0, лишь бы поле
  присутствовало в params.
  Подтверждённый минимальный рабочий набор params для call_in/call_out:
  uniq, duration, source, phone, call_responsible (link — опционален,
  можно не передавать, если пустой).
- sms_in/sms_out: params.uniq НЕ принимается (400 FieldNotExpected).
- Голосовая почта (RingCentral message-store): регистр слова "voicemail"
  РАЗНЫЙ в разных частях API RC — не опечатка, проверено прямыми запросами:
  - eventFilter подписки (создание/обновление подписки на вебхуки):
    только `type=Voicemail` (строчная m). `type=VoiceMail` и
    `messageType=VoiceMail` дают 400 CMN-101 "Parameter [eventFilters]
    value is invalid".
  - Чтение message-store (ListMessagesParameters при выборке сообщений):
    `messageType=VoiceMail` (заглавная M).
  - Значение changes[].type в самом теле вебхука отдельно не проверялось —
    сравнение с ним в коде сделано регистронезависимым на всякий случай.

Call log:
- withRecording=true в call log — намеренный фильтр, отсекает пропущенные
  звонки (голосовая почта из автоответчика туда не попадает). Не убирать
  без учёта: голосовая почта обрабатывается отдельно (см. voicemail-фичу,
  message-store, регистр см. выше), и если withRecording когда-нибудь
  снимут, один пропущенный звонок с голосовым сообщением даст ДВЕ заметки —
  одну от call log (call_in), одну от voicemail-пути (тоже call_in, но с
  отдельным uniq = id голосового сообщения RC, не id звонка).