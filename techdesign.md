# εChat — Technical Design

Документ для разработчиков и AI-агентов. Описывает архитектуру, протокол, ключевые паттерны и инварианты кодовой базы.

---

## Структура проекта

```
src/
├── EChat.Core/          # Бизнес-логика, независимая от платформы
│   ├── Models/          # EF-модели (ChatMessage, Chat, Account, ...)
│   ├── Data/            # ChatDbContext + миграции EF Core
│   ├── Protocol/        # Парсинг/сборка писем, ChatHeaders, OutgoingMessage
│   ├── Transport/       # IMAP/SMTP (MailKit), BatchQueue, IncomingMessageService
│   ├── Sync/            # SyncEngine, NtpTimeService, DeviceSyncService
│   ├── Groups/          # GroupStateManager, GroupMergeEngine
│   ├── Crypto/          # PgpService (PgpCore/BouncyCastle)
│   └── Services/        # FileLogger, ChatEventService, BackupService
├── EChat.UI/            # Razor-компоненты (платформонезависимые)
│   ├── Pages/           # Index, AccountSetup, ChatList, ChatView, Settings, AccountSettings, About
│   ├── Components/      # MessageBubble, ChatListItem, ChatInfoModal
│   └── Services/        # UserContextService, IAppPreferences, IPlatformService, AndroidBackHandler
├── EChat.MAUI/          # .NET MAUI хост (Windows + Android)
│   ├── MauiProgram.cs   # DI-сборка, платформенные пути
│   ├── App.xaml.cs      # Инициализация БД до запуска Blazor
│   └── Platforms/
│       ├── Android/     # MainActivity (OnBackPressed), AndroidManifest.xml, EmailSyncService
│       └── Windows/
├── EChat.Web/           # ASP.NET Core Blazor Server (Docker-деплой)
│   ├── Program.cs       # Настройка, авто-старт транспорта
│   └── Dockerfile       # Alpine multi-stage build
└── EChat.HA/            # Home Assistant интеграция (Python)
```

---

## Зависимости между проектами

```
EChat.MAUI → EChat.UI → EChat.Core
EChat.Web  → EChat.UI → EChat.Core
```

`EChat.Core` не знает ни о Blazor, ни о MAUI.

---

## Модели данных

### ChatMessage
```csharp
MessageId        // Глобально уникальный ID письма (Chat-Message-ID header)
ChatId           // FK → Chat
Sender           // email отправителя
Content          // Текст (расшифрованный)
Timestamp        // Время из Chat-Timestamp header
DisplayTimestamp // С коррекцией NTP-скева
ReceivedAt       // Время сохранения в БД
Status           // Sending | Sent | Read | Failed
ImapUid          // UID в IMAP-папке (для удаления)
ImapFolder       // Папка в IMAP ("eChat", "INBOX")
IsEdited         // Было ли редактировано
InReplyTo        // MessageId цитируемого сообщения
```

**`MessageStatus` enum**:
- `Sending (0)` — сохранено локально, SMTP ещё не подтвердил
- `Sent (1)` — SMTP успешно отправил
- `Read (2)` — получатель открыл чат (пришёл read-receipt)
- `Failed (3)` — постоянная ошибка 5xx, повтор не нужен

### Chat
```csharp
ChatId           // PK (для группы = GroupId)
Type             // OneToOne | Group
AccountId        // Владелец чата
PartnerEmail     // Только для 1:1: email собеседника
Deleted          // Tombstone — не удалять строку, иначе group-create пересоздаст чат
UnreadCount      // Инкрементируется атомарно через ExecuteUpdateAsync
Muted / Archived
LastActivityAt
```

**Tombstone-паттерн**: удалённые чаты помечаются `Deleted=true` и остаются в БД. Это предотвращает повторное создание группы при ресинке IMAP-папки.

### Attachment
```csharp
Id           // Guid
MessageId    // НЕ FK (без каскадного удаления — намеренно)
FilePath     // Абсолютный путь к файлу на диске
FileName / ContentType / Size / Caption / IsImage
```

Файлы хранятся по пути: `{AppDir}/attachments/{MessageId}_{FileName}`

### Account / AccountConfig
- `Account` — персистентная запись в БД (credentials, PGP-ключи)
- `AccountConfig` — мутабельный синглтон в DI, обновляется при `ReconnectAsync`

---

## Платформенные пути

| Платформа | AppDir | DB |
|---|---|---|
| Windows | `%LocalAppData%\echat\` | `{AppDir}/db/echat.db` |
| Android | `/storage/emulated/0/Android/data/com.echat.app/files/` | `{AppDir}/db/echat.db` |
| Web | `/app/data/` (монтируется в Docker) | `{AppDir}/echat.db` |

`FileLogger.AppDir` — канонический способ получить `AppDir` внутри `EChat.Core`. Всегда используй его для записи файлов (вложения, логи), а не `Environment.SpecialFolder.LocalApplicationData` — последнее возвращает **неверный** путь на Android.

---

## Транспортный слой

### Жизненный цикл соединения

```
ChatList.razor → TransportService.ReconnectAsync(account)
  → StopOldIdle()
  → ImapService.DisconnectAsync() + SmtpService.DisconnectAsync()
  → AccountConfig обновляется (email, keys, deviceId)
  → ConnectAsync(imap + smtp)
  → StartSyncLoopAsync()
      → RetryStuckSendingAsync()   // переотправить Sending-сообщения
      → SyncEchatFolderAsync()     // синхронизировать eChat IMAP-папку
      → StartIdleAsync() или polling loop
```

`ReconnectAsync` вызывается при:
- Старте приложения (ChatList.razor OnInitializedAsync)
- Переключении аккаунта
- Сохранении настроек аккаунта

### Получение сообщений

```
ImapService.MessageReceived (event)
  → EmailTransportService.OnMessageReceivedAsync()
      → ChatMessageParser.Parse()          // заголовки + контент
      → ApplyDecryptedContent() если pgp-inline
      → Deduplicator.IsDuplicate()
      → MessagesReceived (event)
          → IncomingMessageService.SaveAsync()
```

`AccountImapWorker` — аналогичный пайплайн для фоновых аккаунтов (не активных).

### Отправка сообщений

```
ChatView.razor → TransportService.SendMessageAsync(OutgoingMessage)
  → Lookup RecipientPublicKey (группа → GroupKeyPairs, 1:1 → Contacts)
  → BatchQueue.Enqueue(message)
      Tier=Immediate → SendSingleAsync() напрямую
      Tier!=Immediate → накапливается, flush по таймеру или при 10+ сообщениях
          → ChatMessageBuilder.BuildSingleAsync() / BuildBatch()
          → SmtpService.SendAsync()
              → SmtpSendResult: Sent | RateLimited | Permanent | TransientError
          → UpdateMessageStatusAsync()
              Sent → Status=Sent
              Permanent → Status=Failed
              RateLimited/TransientError → Status остаётся Sending (ретрай при следующем старте)
```

### SmtpSendResult и обработка ошибок

| Ситуация | Код | Результат | Статус в DB |
|---|---|---|---|
| Успех | — | `Sent` | `Sent` |
| Rate limit | 421 / 429 / 452 | `RateLimited` | остаётся `Sending` |
| Прочие 4xx | — | `TransientError` (3 попытки) | остаётся `Sending` |
| Постоянная ошибка | 5xx | `Permanent` | `Failed` |
| Обрыв после DATA | — | `TransientError` | остаётся `Sending` |

`RetryStuckSendingAsync` при старте берёт все `Status=Sending` и переотправляет. `Failed` не трогает.

---

## Протокол сообщений

### Chat-* заголовки

Все метаданные передаются в заголовках SMTP-письма:

```
Chat-Version: 2.0
Chat-Message-ID: <uuid>@localhost
Chat-Timestamp: 2026-04-11T10:00:00+03:00
Chat-Group-ID: <group-uuid>          # только для группы
Chat-Encryption: pgp-inline          # если зашифровано
Chat-Reaction: 👍
Chat-Reaction-To: <target-msg-id>
Chat-Edit-Of: <target-msg-id>
Chat-Edit-Version: 2
Chat-Delete-Of: <target-msg-id>
Chat-Read-Of: id1,id2,id3
Chat-System-Type: group-create       # системное сообщение
Chat-Sync-Type: read-state           # синхронизация между устройствами
Chat-Sync-Device-ID: <device-uuid>
Autocrypt: addr=user@example.com; keydata=<base64-pubkey>
In-Reply-To: <target-msg-id>
```

### Шифрование (pgp-inline)

**Незашифрованное письмо**: все Chat-* заголовки снаружи, тело = plaintext.

**Зашифрованное письмо**: внешние заголовки только `Chat-Version`, `Chat-Group-ID`, `Autocrypt`. Всё остальное — внутри PGP-зашифрованного тела:

```
Chat-Message-ID: <uuid>
Chat-Timestamp: ...
Chat-In-Reply-To: ...
<прочие метаданные>
                           ← пустая строка-разделитель
Текст сообщения

--echat-att--              ← блок вложения (если есть)
Content-Type: image/jpeg
Content-Filename: photo.jpg
Content-Size: 12345

<base64-данные файла>
--echat-att-end--
```

`ChatMessageBuilder.BuildInnerContent()` собирает этот формат.  
`ChatMessageParser.ApplyDecryptedContent()` парсит его после расшифровки.

**Важно**: `email.Attachments` (MimeKit) содержит только вложения с `Content-Disposition: attachment` — для зашифрованных писем они недоступны. Поэтому вложения кодируются в текстовый блок внутри шифрограммы.

### Батчинг

`BatchKey = {Recipients (HashSet), GroupId, Tier}`. Сообщения одного ключа упаковываются в `multipart/mixed` с частями `message/rfc822`. Тиры:
- `Immediate` — немедленная отправка, без батча
- `System` — быстрый батч (системные сообщения)
- `LowPriority` — медленный батч (читабельность, экономия лимитов)

---

## База данных

**СУБД**: SQLite с WAL-mode (`PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;`).

**Важные индексы**:
- `Messages(MessageId)` — дедупликация
- `Messages(ChatId, Timestamp)` — загрузка чата
- `Chats(AccountId, PartnerEmail)` — роутинг 1:1
- `Chats(Archived, LastActivityAt)` — список чатов

**DateTimeOffset и SQLite**: EF Core не умеет транслировать сравнения `DateTimeOffset` в WHERE-условия на SQLite. Паттерн: загружай все строки в память (`.ToListAsync()`), фильтруй в C#. ORDER BY работает нормально (хранится как ISO-8601 TEXT).

**EF Core scoped context**: `ChatDbContext` — scoped, не singleton. Используй `IServiceScopeFactory.CreateScope()` в синглтонах:
```csharp
using var scope = _scopeFactory.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
```

**Инкремент UnreadCount**: только через `ExecuteUpdateAsync` — атомарно:
```csharp
await db.Chats
    .Where(c => c.ChatId == chatId)
    .ExecuteUpdateAsync(s => s.SetProperty(c => c.UnreadCount, c => c.UnreadCount + 1));
```

---

## UI

### Разделение платформ

`IPlatformService.IsDesktop` — единственный способ разветвления UI. На Windows — трёхколоночный layout (ChatList слева, чат справа). На Android — последовательная навигация через Blazor Router.

`ChatList.razor` содержит оба layout внутри одного файла, переключение через `@if (Platform.IsDesktop)`.

### Мобильная кнопка "Назад" (Android)

`AndroidBackHandler` (static класс в `EChat.UI/Services/`):
- `ChatView.razor` при инициализации вызывает `AndroidBackHandler.Register(() => InvokeAsync(GoBack))`
- `ChatView.razor.Dispose()` вызывает `AndroidBackHandler.Unregister()`
- `MainActivity.OnBackPressedDispatcher.AddCallback()` вызывает `AndroidBackHandler.TriggerBack()`

Использует `OnBackPressedDispatcher`, а **не** `override OnBackPressed()` — последний не работает с жестовой навигацией на Android 13+.

### MessageBubble

Компонент `EChat.UI/Components/MessageBubble.razor` — используется и в десктопе, и в мобиле. Ключевые детали:
- `GetAttachmentUrl(att)` читает `att.FilePath` с диска, возвращает `data:{contentType};base64,...`. Если файл не найден — возвращает `""`, изображение не показывается.
- Лайтбокс: `_lightboxUrl` поле, `@onclick` на картинке открывает fullscreen overlay
- `MobileMode` prop отключает контекстное меню (на мобиле — action bar вверху)

### CSS

Глобальные стили: `src/EChat.MAUI/wwwroot/css/app.css` и `src/EChat.UI/wwwroot/css/app.css`. Стили контекстного меню (`.ctx-menu`, `.ctx-item`, etc.) должны быть в **глобальном** CSS, а не в inline `<style>` компонента — иначе они не загружаются в пустом чате.

---

## Многоаккаунтность

- Только один аккаунт — `IsActive=true` — обслуживается `EmailTransportService`
- Остальные аккаунты — фоновые воркеры `AccountImapWorker`, управляемые `MultiAccountImapManager`
- При переключении аккаунта: `ChatEventService.NotifyAccountSwitched()` → MultiAccountImapManager перезапускает воркеры

---

## Синхронизация устройств

При отправке сообщения отправитель всегда добавляет себя в CC. Другое устройство с тем же ящиком получает письмо, парсит его как `isSentSync=true` (sender == accountEmail) и сохраняет со статусом `Sent` (не инкрементируя UnreadCount).

`DeviceSyncService` отправляет специальные sync-сообщения (`Chat-Sync-Type: read-state`), которые `IncomingMessageService` обрабатывает отдельно.

---

## Логирование

`FileLogger` — синглтон. Уровни: `None | Error | Warn | Info | Debug`. Настраивается в Settings UI, сохраняется в `Settings` таблице (`log_level` ключ).

При старте `ChatList.razor` читает `log_level` из `Prefs` и применяет к `FileLogger.MinLevel`.

```csharp
_fileLogger.Write("INFO", "MyService", $"Something happened: {detail}");
```

Категория — произвольная строка, обычно имя класса или метода.

---

## Сборка и публикация

### Разработка

```bash
# Windows desktop (для разработки)
dotnet run --project src/EChat.MAUI -f net10.0-windows10.0.19041.0

# Android (нужен эмулятор или устройство)
dotnet build src/EChat.MAUI -f net10.0-android -c Release -t:SignAndroidPackage

# Core unit tests
dotnet build src/EChat.Core -c Release
```

### Полная сборка всех платформ

```bat
publish.bat
```

Скрипт:
1. Чистит `bin/obj` всех проектов
2. Собирает Windows desktop → `pub/win/`, переименовывает `EChat.Maui.exe` → `echat.exe`
3. Упаковывает `pub/distr/EChat-win.zip`
4. Запускает Inno Setup → `pub/distr/EChat-Setup-x.x.x.exe`
5. Собирает Android APK → `pub/android/` и `pub/distr/EChat.apk`
6. Собирает Docker-образ, пушит в локальный registry и Docker Hub
7. Генерирует `pub/distr/docker-compose.yml`
8. Копирует `pub/distr/*` в `e:\YandexDisk\share\echat\`

### Версионирование

Версия хранится в нескольких файлах:
- `src/EChat.Core/version.txt`
- `src/EChat.UI/version.txt`
- `src/EChat.MAUI/version.txt`
- `src/EChat.Web/version.txt`

Формат: `0.1.167`. Инкрементируется автоматически сборочным скриптом (`.targets` файл или custom build step).

---

## Docker / Web деплой

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
# ... multi-stage build ...
ENTRYPOINT ["dotnet", "EChat.Web.dll"]
```

`EChat.Web/Program.cs`:
- Data dir: `ECHAT_DATA_DIR` env var → `EChat:DataDir` config → `{ContentRoot}/data`
- DB: `{dataDir}/echat.db`
- При старте: `InitializeEChatDatabaseAsync()` → авто-загрузка активного аккаунта → `ReconnectAsync()`
- SSL не используется — ожидается termination на nginx/Traefik

---

## Инварианты и ловушки

1. **Никогда не используй `Environment.SpecialFolder.LocalApplicationData` для путей к файлам** — на Android это внутреннее хранилище, недоступное приложению. Используй `_fileLogger.AppDir`.

2. **DateTimeOffset в WHERE-условиях EF Core + SQLite** — не работает. Загружай строки, фильтруй в C#.

3. **`email.Attachments` (MimeKit) для зашифрованных писем** — возвращает пустой список. Вложения кодируются в `--echat-att--` блоки внутри шифрограммы.

4. **`OnBackPressed()` override в Android 13+** — не срабатывает при жестовой навигации. Использовать только `OnBackPressedDispatcher.AddCallback()`.

5. **CSS inline `<style>` в компонентах** — инжектируется только при рендере компонента. Если компонент не рендерится (пустой чат), стили не загружаются. Общие стили — только в `app.css`.

6. **`MessageId` для дедупликации** — глобально уникален (UUID@localhost). Проверяется в `IncomingMessageService` перед сохранением. IMAP-папка eChat как вторичный источник дедупликации.

7. **UnreadCount** — инкрементируется только при `!isSentSync`. Не обновляй напрямую через EF-трекинг — используй `ExecuteUpdateAsync` для атомарности.

8. **Tombstone при удалении чата** — устанавливай `Deleted=true`, не удаляй строку `Chat`. Иначе при ресинке eChat-папки `group-create` пересоздаст чат.

9. **Шифрование группы** — при `group-create` приватный ключ группы отправляется каждому участнику персонально (зашифрован его публичным ключом). Последующие сообщения шифруются публичным ключом группы.

10. **`BatchTier.Immediate`** — единственный тир, который точно отправляется без задержки. Все пользовательские сообщения из чата используют `Immediate`.
