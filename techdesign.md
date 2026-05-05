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
│   └── Services/        # FileLogger, ChatEventService, BackupService, VersionInfo
├── EChat.UI/            # Razor-компоненты (платформонезависимые)
│   ├── Pages/           # Index, AccountSetup, ChatList, ChatList.razor.cs,
│   │                    # ChatView, Settings, AccountSettings, About
│   ├── Components/      # MessageBubble, ChatListItem, ChatInfoModal, NewChatModal
│   └── Services/        # UserContextService, IAppPreferences, IPlatformService,
│                        # AndroidBackHandler
├── EChat.MAUI/          # .NET MAUI хост (Windows + Android)
│   ├── MauiProgram.cs   # DI-сборка, платформенные пути, VersionInfo.VersionOverride
│   ├── App.xaml.cs      # Инициализация, запуск транспорта и foreground-сервиса
│   └── Platforms/
│       ├── Android/     # MainActivity, MainApplication, AndroidManifest.xml,
│       │                # EmailSyncService, MessageNotificationHelper
│       └── Windows/     # TaskbarBadgeHelper, TaskbarFlashHelper
├── EChat.Web/           # ASP.NET Core Blazor Server (Docker-деплой)
│   ├── Program.cs       # Настройка, авто-старт транспорта, VersionInfo.VersionOverride
│   └── Dockerfile       # Alpine multi-stage build
└── scripts/
    └── bump-versions.ps1 # Умная инкрементация версий перед публикацией
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
ChatId           // PK — случайный UUID, независимый от GroupId
Type             // OneToOne | Group
AccountId        // Владелец чата
ContactEmail     // FK → Contact.Email — только для 1:1: email собеседника
GroupId          // FK → ChatGroup.GroupId — только для Group: ссылка на группу
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
FilePath     // Имя файла относительно AttachmentsDir (новые записи)
             // или абсолютный путь (старые записи — ResolveFilePath умеет оба формата)
FileName / ContentType / Size / Caption / IsImage
```

Файлы хранятся по пути: `{AppDir}/attachments/{MessageId}_{FileName}`

`DatabasePathInfo.ResolveFilePath(stored)` — всегда используй этот метод для получения абсолютного пути из `att.FilePath`. Обрабатывает оба формата (относительный и абсолютный).

### Contact
```csharp
AccountId       // PK part 1 — владелец контакта
Email           // PK part 2 — email контакта
DisplayName     // Имя, редактируемое пользователем
PublicKey       // PGP-ключ контакта (для шифрования исходящих)
KeyFingerprint  // Fingerprint для отображения в ChatInfoModal
Verified        // Верифицирован через invite-flow
IsBlocked       // Заблокирован
BlockedAt       // Время блокировки
Notes           // Заметки пользователя
```

**Изоляция по аккаунту**: PK — `(AccountId, Email)`. Контакт из ящика A не виден из ящика B.

### GroupMember
```csharp
GroupId      // PK part 1
MemberEmail  // PK part 2
Role         // Admin | Member
AddedAt / AddedBy
NameColor    // Цвет имени в чате
DisplayName  // Имя участника — приходит в протоколе (group-create/group-member-add)
             // Fallback-цепочка: GroupMember.DisplayName → Contact.DisplayName → полный email
```

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
App.xaml.cs (Task.Run) → TransportService.ReconnectAsync(account)
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
- Старте приложения (из `App.xaml.cs`, не из Blazor)
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
ChatList/ChatView → TransportService.SendMessageAsync(OutgoingMessage)
  → Lookup RecipientPublicKey (группа → GroupKeyPairs, 1:1 → Contacts)
  → BatchQueue.Enqueue(message)
      Tier=Immediate → SendSingleAsync() напрямую
      Tier!=Immediate → накапливается, flush по таймеру или при 10+ сообщениях
          → ChatMessageBuilder.BuildSingleAsync() / BuildBatch()
          → SmtpService.SendAsync()
              → SmtpSendResult: Sent | RateLimited | Permanent | TransientError
          → UpdateMessageStatusAsync()
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

**Важно**: `email.Attachments` (MimeKit) содержит только вложения с `Content-Disposition: attachment` — для зашифрованных писем они недоступны. Поэтому вложения кодируются в текстовый блок внутри шифрограммы.

### Батчинг

`BatchKey = {Recipients (HashSet), GroupId, Tier}`. Тиры:
- `Immediate` — немедленная отправка, без батча (все пользовательские сообщения)
- `System` — быстрый батч (системные сообщения)
- `LowPriority` — медленный батч (экономия лимитов)

---

## База данных

**СУБД**: SQLite с WAL-mode (`PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;`).

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

**DateTimeOffset и SQLite**: EF Core не умеет транслировать сравнения `DateTimeOffset` в WHERE-условия на SQLite. Паттерн: загружай строки в память (`.ToListAsync()`), фильтруй в C#. ORDER BY работает нормально.

---

## UI

### Разделение платформ

`IPlatformService.IsDesktop` — единственный способ разветвления UI. На Windows — трёхколоночный layout (ChatList слева, чат справа). На Android — последовательная навигация через Blazor Router.

`ChatList.razor` содержит оба layout внутри одного файла, переключение через `@if (Platform.IsDesktop)`.

Вся логика `ChatList` вынесена в code-behind файл `ChatList.razor.cs` (partial class).

### Компоненты

| Компонент | Описание |
|---|---|
| `MessageBubble` | Рендер одного сообщения. Используется и на десктопе, и на мобиле |
| `ChatListItem` | Элемент списка чатов |
| `ChatInfoModal` | Информация о чате / участниках группы |
| `NewChatModal` | Модальное окно создания чата (3 вкладки: My Invite, Add Contact, New Group) |

### IPlatformService

Интерфейс для платформенных операций. Реализации: `PlatformService` (MAUI) и `WebPlatformService` (Web).

```csharp
bool IsDesktop                          // Windows = true, остальные = false
bool SupportsMauiFilePicker             // MAUI = true, Web = false
bool SupportsPickFolder                 // Android = true (SAF), остальные = false
bool SupportsBackgroundNotificationToggle // Android = true, остальные = false

Task SaveFileAsync(...)                 // Нативный диалог сохранения
Task<Stream?> PickFileAsync(...)        // Нативный файловый пикер
Task OpenAttachmentAsync(...)           // Открыть файл через нативное приложение
Task<bool> SaveToDownloadsAsync(...)    // Сохранить в папку Загрузки (Android/Windows)
Task<bool> SaveToPickedFolderAsync(...) // SAF диалог (Android)
void UpdateBadge(int totalUnread)       // Счётчик на иконке (Windows taskbar)
void RestartApp()                       // Перезапуск приложения
Task SetBackgroundNotificationVisibleAsync(bool) // BG уведомление (Android)
Task OpenBatteryOptimizationSettingsAsync()      // Диалог батареи (Android)
```

На Web все методы-no-op возвращают `Task.CompletedTask` / `false`.

### Мобильная кнопка "Назад" (Android)

`AndroidBackHandler` (static класс в `EChat.UI/Services/`):
- `ChatView.razor` при инициализации регистрирует callback
- `ChatView.razor.Dispose()` отменяет регистрацию
- `MainActivity` использует `OnBackPressedDispatcher.AddCallback()` (не `override OnBackPressed()` — не работает с жестовой навигацией на Android 13+)

### MessageBubble

- `GetAttachmentUrl(att)` — читает файл по `DbPathInfo.ResolveFilePath(att.FilePath)`, возвращает `data:{contentType};base64,...`
- **Лайтбокс**: открывается по клику на картинку. На мобиле поддерживает pinch-to-zoom (×1–×6), двойной тап (переключение ×1/×2.5), pan одним пальцем. JS-функции: `initLightboxZoom`, `isLightboxZoomed`, `resetLightboxZoom` в `index.html`
- Тап по фону при zoom > 1 — сначала сбрасывает zoom; при zoom = 1 — закрывает
- `MobileMode` prop отключает контекстное меню (на мобиле — action bar вверху)

### CSS

Глобальные стили: `src/EChat.MAUI/wwwroot/css/app.css` и `src/EChat.UI/wwwroot/css/app.css`.

**Важно**: inline `<style>` в компонентах инжектируются только при рендере. Общие стили (`.ctx-menu`, etc.) — только в `app.css`, иначе не загрузятся в пустом чате.

---

## Многоаккаунтность

- Один аккаунт — `IsActive=true` — обслуживается `EmailTransportService`
- Остальные — фоновые воркеры `AccountImapWorker`, управляемые `MultiAccountImapManager`
- При переключении: `ChatEventService.NotifyAccountSwitched()` → воркеры перезапускаются

---

## Синхронизация устройств

При отправке сообщения отправитель добавляет себя в CC. Другое устройство с тем же ящиком получает письмо, парсит его как `isSentSync=true` (sender == accountEmail) и сохраняет со статусом `Sent` без инкремента UnreadCount.

`DeviceSyncService` отправляет sync-сообщения (`Chat-Sync-Type: read-state`), которые `IncomingMessageService` обрабатывает отдельно.

---

## Версионирование

### Файлы версий

Каждый проект имеет свой `version.txt` (отслеживается в git):
```
src/EChat.Core/version.txt    # Например: 0.2.17
src/EChat.UI/version.txt
src/EChat.MAUI/version.txt
src/EChat.Web/version.txt
```

Версия читается в `.csproj` через MSBuild: `$([System.IO.File]::ReadAllText('version.txt'))`.  
`InformationalVersion` формируется как `{version}+{yyyyMMddHHmm}` — каждая сборка уникально штампована.

`UpToDateCheckInput` в каждом `.csproj` гарантирует пересборку при изменении `version.txt`.

### Отображение версии

`VersionInfo.VersionOverride` (static) — устанавливается хост-проектом при старте:
- `MauiProgram.cs` — из `AssemblyInformationalVersionAttribute` MAUI-сборки
- `Web/Program.cs` — из `Assembly.GetExecutingAssembly()`

Это гарантирует, что экран About показывает версию хоста, а не `EChat.Core`.

`VersionInfo.BuildDate` парсит `yyyyMMddHHmm` суффикс и форматирует как `"20260424 15:23"`.

### Скрипт bump-versions.ps1

`scripts/bump-versions.ps1` — умная инкрементация перед публикацией:

```powershell
# Режимы:
bump-versions.ps1            # all: Core/UI если изменились + MAUI + Web
bump-versions.ps1 -Mode win  # Core/UI если изменились + MAUI
bump-versions.ps1 -Mode web  # Core/UI если изменились + Web
bump-versions.ps1 -Diagnose  # Показать изменённые файлы без бампа
```

Обнаружение изменений: SHA256-хэш всех `.cs`, `.razor`, `.csproj` файлов проекта (кроме `bin/` и `obj/`) хранится в `.src-hash` рядом с проектом. При следующем запуске хэши сравниваются.

`.src-hash` добавлен в `.gitignore`.

---

## Android — Фоновая работа

### EmailSyncService

Foreground-сервис (`ForegroundService.TypeDataSync`). Запускается из `App.xaml.cs` после `ReconnectAsync`. Держит процесс живым пока Android не убьёт его принудительно.

`StartCommandResult.Sticky` — Android перезапускает сервис после убийства. При перезапуске `IPlatformApplication.Current` может быть `null` пока MAUI не инициализировался. Сервис ждёт до 10 секунд с ретраями.

**Видимость уведомления**: после `StartForeground()` (обязательного) читается `bg_notification_visible` из `IAppPreferences`. Если `false` — сразу вызывается `StopForeground(StopForegroundFlags.Remove)`. Сервис продолжает работать, уведомление исчезает.

### Жизненный цикл Activity

`MainActivity` содержит статический флаг `_processProperlyStarted`. При восстановлении Activity после убийства процесса Android'ом (`savedInstanceState != null && !_processProperlyStarted`) — Activity перезапускается чисто через `StartActivity + Finish`. Это предотвращает пустой экран: Blazor WebView не умеет восстанавливать состояние из `savedInstanceState`.

### Глобальная обработка исключений

`MainApplication.cs` регистрирует:
- `TaskScheduler.UnobservedTaskException` — `SetObserved()` + лог в `Android.Util.Log`. Без этого необработанные исключения в `Task.Run` убивают процесс
- `AppDomain.CurrentDomain.UnhandledException` — лог перед крашем

---

## Логирование

`FileLogger` — синглтон. Уровни: `None | Error | Warn | Info | Debug`. Настраивается в Settings UI.

```csharp
_fileLogger.Write("INFO", "MyService", $"Something happened: {detail}");
```

На Android дополнительно: `Android.Util.Log.Debug/Error("eChat", message)` — видно в logcat.

---

## Сборка и публикация

### Разработка

```bash
# Windows desktop
dotnet run --project src/EChat.MAUI -f net10.0-windows10.0.19041.0

# Android (нужен эмулятор или устройство)
dotnet build src/EChat.MAUI -f net10.0-android -c Release -t:SignAndroidPackage
```

### Публикация

```bat
publish.bat       # Все платформы: Core/UI (если изменились) + MAUI + Web
publish-win.bat   # Только Windows: Core/UI (если изменились) + MAUI
```

`publish.bat` выполняет:
1. `scripts/bump-versions.ps1` — инкрементирует версии
2. Собирает Windows desktop → `pub/win/`, создаёт `pub/distr/EChat-win.zip`
3. Запускает Inno Setup → `pub/distr/EChat-Setup-x.x.x.exe`
4. Собирает Android APK → `pub/distr/EChat.apk`
5. Собирает и пушит Docker-образ, генерирует `pub/distr/docker-compose.yml`
6. Копирует дистрибутивы в `e:\YandexDisk\share\echat\`

Папки `pub/` и `.claude/` исключены из git через `.gitignore`.

### Docker (EChat.Web)

`Dockerfile` (multi-stage, Alpine):
- Копирует `*.csproj` **и** `version.txt` каждого проекта перед `dotnet restore` (MSBuild читает `version.txt` при вычислении свойств проекта)
- Data dir: `ECHAT_DATA_DIR` env var → `EChat:DataDir` config → `{ContentRoot}/data`
- SSL не используется — ожидается termination на nginx/Traefik

---

## Безопасность credentials

### ICredentialProtector
Интерфейс для шифрования/дешифрования чувствительных полей (пароль IMAP, приватный PGP-ключ) в SQLite.

```csharp
string Protect(string plaintext)      // идемпотентно: уже зашифрованное возвращает as-is
string Unprotect(string ciphertext)   // legacy plaintext (без префикса) возвращает as-is
bool IsProtected(string storedValue)  // true если несёт платформенный префикс
```

Реализации:
- `DpapiCredentialProtector` (Windows) — префикс `dpapi:`, entropy `"echat-cred-v1"`
- `SecureStorageCredentialProtector` (Android) — префикс `aes:`, AES-256-GCM через Android Keystore
- `PlaintextCredentialProtector` — no-op, для Web и разработки

**Step 3d при старте**: читает сырые значения из БД через прямой `SqliteConnection` (минуя EF Value Converter), проверяет `IsProtected()`. Если все уже зашифрованы — SaveChanges не вызывается.

---

## Группы — протокол передачи DisplayName

В сообщениях типа `group-create`, `group-member-add`, `group-member-remove` передаются имена участников:

```json
// group-create
{
  "type": "group-create",
  "group_id": "...",
  "members": ["a@x.com", "b@x.com"],
  "member_names": { "a@x.com": "Alice", "b@x.com": "Bob" }
}

// group-member-add
{ "type": "group-member-add", "added_email": "c@x.com", "added_name": "Carol", "added_by": "a@x.com" }

// group-member-remove
{ "type": "group-member-remove", "removed_email": "b@x.com", "removed_name": "Bob", "removed_by": "a@x.com" }
```

`IncomingMessageService` сохраняет `DisplayName` в `GroupMember` при создании; обновляет если пришло лучшее имя.

Fallback-цепочка при отображении: `GroupMember.DisplayName` → `Contact.DisplayName` → полный email (никогда `email.Split('@')[0]`).

---

## NTP синхронизация времени

`NtpTimeService` корректирует `DateTimeOffset.UtcNow` через атомарный `_offsetTicks`.

**Важно**: `socket.ReceiveTimeout` игнорируется `await ReceiveAsync`. Таймаут реализован через `CancellationTokenSource(5s)`, переданный в `ConnectAsync` / `SendAsync` / `ReceiveAsync`.

Валидация NTP-ответа:
1. `received < 48` → `InvalidDataException`
2. `intPart == 0` → `InvalidDataException` (нулевой timestamp = мусор)
3. `|networkDateTime - UtcNow| > 3650 дней` → `InvalidDataException` (санитарная проверка)

При сбое NTP — HTTP HEAD fallback к mail-доменам подключённых аккаунтов (из заголовка `Date`).

---

## Инварианты и ловушки

1. **Никогда не используй `Environment.SpecialFolder.LocalApplicationData` для путей к файлам** — на Android это внутреннее хранилище. Используй `_fileLogger.AppDir` или `DatabasePathInfo.AttachmentsDir`.

2. **`DatabasePathInfo.ResolveFilePath()`** — всегда используй этот метод для получения абсолютного пути вложения. Обрабатывает как старые (абсолютные) пути, так и новые (относительные имена файлов).

3. **DateTimeOffset в WHERE-условиях EF Core + SQLite** — не работает. Загружай строки, фильтруй в C#.

4. **`email.Attachments` (MimeKit) для зашифрованных писем** — возвращает пустой список. Вложения кодируются в `--echat-att--` блоки внутри шифрограммы.

5. **`OnBackPressed()` override в Android 13+** — не срабатывает при жестовой навигации. Использовать только `OnBackPressedDispatcher.AddCallback()`.

6. **CSS inline `<style>` в компонентах** — инжектируется только при рендере компонента. Если компонент не рендерится (пустой чат), стили не загружаются. Общие стили — только в `app.css`.

7. **`MessageId` для дедупликации** — глобально уникален (UUID@localhost). Проверяется в `IncomingMessageService` перед сохранением.

8. **UnreadCount** — инкрементируется только при `!isSentSync`. Не обновляй напрямую через EF-трекинг — используй `ExecuteUpdateAsync` для атомарности.

9. **Tombstone при удалении чата** — устанавливай `Deleted=true`, не удаляй строку `Chat`. Иначе при ресинке eChat-папки `group-create` пересоздаст чат.

10. **Шифрование группы** — при `group-create` приватный ключ группы отправляется каждому участнику персонально (зашифрован его публичным ключом). Последующие сообщения шифруются публичным ключом группы.

11. **`BatchTier.Immediate`** — единственный тир без задержки. Все пользовательские сообщения используют `Immediate`.

12. **Оптимистичный UI при отправке с вложением** — после записи файла на диск и до `StateHasChanged()` нужно добавить attachment entities в `_messageAttachments[msgId]`. Иначе картинка не появится до перезапуска (актуально для обоих путей: `ChatList.razor.cs` и `ChatView.razor`).

13. **`version.txt` в Dockerfile** — должен копироваться вместе с `.csproj` перед `dotnet restore`. MSBuild вычисляет `<Version>` из `version.txt` при парсинге `.csproj`, а не при компиляции.

14. **`TaskScheduler.UnobservedTaskException` на Android** — без `SetObserved()` необработанное исключение в `Task.Run` через некоторое время убивает процесс во время GC. Обязательно регистрируй в `MainApplication`.

15. **`PRAGMA foreign_keys` внутри транзакции EF** — SQLite молча игнорирует `PRAGMA foreign_keys = OFF/ON` внутри активной транзакции. Для миграций, требующих пересборки таблиц с FK, используй `migrationBuilder.Sql("PRAGMA ...", suppressTransaction: true)` — EF коммитит транзакцию перед выполнением.

16. **`Contact.Split('@')[0]` — никогда** — префикс email не является именем пользователя. Для отображения используй `DisplayName ?? полный_email`. Fallback `Split('@')[0]` удалён из всего кодовой базы.

17. **EF Value Converter не срабатывает при чтении через `SELECT`** — конвертеры (например, шифрование паролей) применяются только при записи через EF. Чтение raw-значений для проверки `IsProtected()` требует прямого `SqliteConnection`, не `DbContext`.

18. **`GroupMember.DisplayName` — источник правды для имён в группе** — не делай лукап в `Contacts` когда нужно имя участника группы. Contacts может не знать о человеке (добавлен другим участником). `GroupMember.DisplayName` заполняется из протокола и всегда актуален.
