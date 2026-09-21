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
- sms_in/sms_out: params.uniq НЕ принимается (400 FieldNotExpected).