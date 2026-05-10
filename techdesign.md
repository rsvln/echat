# εChat — Technical Design

Reference document for developers and AI agents. Describes architecture, protocol, key patterns, and codebase invariants.

---

## Project structure

```
src/
├── EChat.Core/          # Platform-independent business logic
│   ├── Models/          # EF models (ChatMessage, Chat, Account, ...)
│   ├── Data/            # ChatDbContext + EF Core migrations
│   ├── Protocol/        # Email parsing/building, ChatHeaders, OutgoingMessage
│   ├── Transport/       # IMAP/SMTP (MailKit), BatchQueue, IncomingMessageService
│   ├── Sync/            # SyncEngine, NtpTimeService, DeviceSyncService
│   ├── Groups/          # GroupStateManager, GroupMergeEngine
│   ├── Crypto/          # PgpService (PgpCore/BouncyCastle)
│   └── Services/        # FileLogger, ChatEventService, BackupService,
│                        # UpdateService, VersionInfo
├── EChat.UI/            # Razor components (platform-independent)
│   ├── Pages/           # Index, AccountSetup, ChatList, ChatList.razor.cs,
│   │                    # ChatView, Settings, AccountSettings, About
│   ├── Components/      # MessageBubble, ChatListItem, ChatInfoModal, NewChatModal
│   └── Services/        # UserContextService, IAppPreferences, IPlatformService,
│                        # AndroidBackHandler
├── EChat.MAUI/          # .NET MAUI host (Windows + Android)
│   ├── MauiProgram.cs   # DI setup, platform paths, VersionInfo.VersionOverride
│   ├── App.xaml.cs      # Initialisation, transport start, foreground service
│   └── Platforms/
│       ├── Android/     # MainActivity, MainApplication, AndroidManifest.xml,
│       │                # EmailSyncService, MessageNotificationHelper
│       └── Windows/     # TaskbarBadgeHelper, TaskbarFlashHelper
├── EChat.Web/           # ASP.NET Core Blazor Server (Docker deployment)
│   ├── Program.cs       # Setup, auto-start transport, VersionInfo.VersionOverride
│   └── Dockerfile       # Alpine multi-stage build
└── scripts/
    └── bump-versions.ps1 # Smart version increment before publishing
```

---

## Project dependencies

```
EChat.MAUI → EChat.UI → EChat.Core
EChat.Web  → EChat.UI → EChat.Core
```

`EChat.Core` has no knowledge of Blazor or MAUI.

---

## Data models

### ChatMessage
```csharp
MessageId        // Globally unique email message ID (Chat-Message-ID header)
ChatId           // FK → Chat
Sender           // Sender email address
Content          // Message text (decrypted)
Timestamp        // Time from Chat-Timestamp header
DisplayTimestamp // NTP-skew-corrected timestamp
ReceivedAt       // Time the message was saved to DB
Status           // Sending | Sent | Read | Failed
ImapUid          // UID in the IMAP folder (for deletion)
ImapFolder       // IMAP folder name ("eChat", "INBOX")
IsEdited         // Whether the message has been edited
InReplyTo        // MessageId of the quoted message
```

**`MessageStatus` enum**:
- `Sending (0)` — saved locally, SMTP not yet confirmed
- `Sent (1)` — SMTP delivery confirmed
- `Read (2)` — recipient opened the chat (read receipt received)
- `Failed (3)` — permanent 5xx error, no retry

### Chat
```csharp
ChatId           // PK — random UUID, independent of GroupId
Type             // OneToOne | Group
AccountId        // Chat owner
ContactEmail     // FK → Contact.Email — 1:1 chats only: the other party's email
GroupId          // FK → ChatGroup.GroupId — group chats only
Deleted          // Tombstone — do not delete the row, otherwise group-create will recreate the chat
UnreadCount      // Incremented atomically via ExecuteUpdateAsync
Muted / Archived
LastActivityAt
```

**Tombstone pattern**: deleted chats are marked `Deleted=true` and kept in the database. This prevents a group chat from being recreated when the eChat IMAP folder is re-synced.

### Attachment
```csharp
Id           // Guid
MessageId    // NOT a FK (no cascade delete — intentional)
FilePath     // File name relative to AttachmentsDir (new records)
             // or absolute path (legacy records — ResolveFilePath handles both)
FileName / ContentType / Size / Caption / IsImage
```

Files are stored at: `{AppDir}/attachments/{MessageId}_{FileName}`

`DatabasePathInfo.ResolveFilePath(stored)` — always use this method to get an absolute path from `att.FilePath`. Handles both relative and absolute formats.

### Contact
```csharp
AccountId       // PK part 1 — contact owner
Email           // PK part 2 — contact's email
DisplayName     // User-editable display name
PublicKey       // Contact's PGP key (for encrypting outgoing messages)
KeyFingerprint  // Fingerprint shown in ChatInfoModal
Verified        // Verified through the invite flow
IsBlocked       // Blocked
BlockedAt       // Time of blocking
Notes           // User notes
```

**Account isolation**: PK is `(AccountId, Email)`. Contacts from account A are invisible from account B.

### GroupMember
```csharp
GroupId      // PK part 1
MemberEmail  // PK part 2
Role         // Admin | Member
AddedAt / AddedBy
NameColor    // Display name colour in the chat
DisplayName  // Member name — comes from the protocol (group-create/group-member-add)
             // Fallback chain: GroupMember.DisplayName → Contact.DisplayName → full email
```

### Account / AccountConfig
- `Account` — persistent DB record (credentials, PGP keys)
- `AccountConfig` — mutable singleton in DI, updated on `ReconnectAsync`

---

## Platform paths

| Platform | AppDir | DB |
|---|---|---|
| Windows | `%LocalAppData%\echat\` | `{AppDir}/db/echat.db` |
| Android | `/storage/emulated/0/Android/data/com.echat.app/files/` | `{AppDir}/db/echat.db` |
| Web | `/app/data/` (Docker volume mount) | `{AppDir}/echat.db` |

`FileLogger.AppDir` — the canonical way to get `AppDir` inside `EChat.Core`. Always use it for writing files (attachments, logs) instead of `Environment.SpecialFolder.LocalApplicationData`, which returns the **wrong** path on Android.

---

## Transport layer

### Connection lifecycle

```
App.xaml.cs (Task.Run) → TransportService.ReconnectAsync(account)
  → StopOldIdle()
  → ImapService.DisconnectAsync() + SmtpService.DisconnectAsync()
  → AccountConfig updated (email, keys, credentials)
  → ConnectAsync(imap + smtp)
  → StartSyncLoopAsync()
      → RetryStuckSendingAsync()   // retry Sending messages
      → SyncEchatFolderAsync()     // sync eChat IMAP folder
      → StartIdleAsync() or polling loop
```

`ReconnectAsync` is called on:
- App start (from `App.xaml.cs`, not from Blazor)
- Account switch
- Account settings saved

### Receiving messages

```
ImapService.MessageReceived (event)
  → EmailTransportService.OnMessageReceivedAsync()
      → ChatMessageParser.Parse()          // headers + content
      → ApplyDecryptedContent() if pgp-inline
      → Deduplicator.IsDuplicate()
      → MessagesReceived (event)
          → IncomingMessageService.SaveAsync()
```

`AccountImapWorker` — identical pipeline for background (non-active) accounts.

### Sending messages

```
ChatList/ChatView → TransportService.SendMessageAsync(OutgoingMessage)
  → Lookup RecipientPublicKey (group → GroupKeyPairs, 1:1 → Contacts)
  → BatchQueue.Enqueue(message)
      Tier=Immediate → SendSingleAsync() directly
      Tier!=Immediate → accumulated, flushed on timer or at 10+ messages
          → ChatMessageBuilder.BuildSingleAsync() / BuildBatch()
          → SmtpService.SendAsync()
              → SmtpSendResult: Sent | RateLimited | Permanent | TransientError
          → UpdateMessageStatusAsync()
```

### SmtpSendResult and error handling

| Situation | Code | Result | DB status |
|---|---|---|---|
| Success | — | `Sent` | `Sent` |
| Rate limit | 421 / 429 / 452 | `RateLimited` | stays `Sending` |
| Other 4xx | — | `TransientError` (3 retries) | stays `Sending` |
| Permanent error | 5xx | `Permanent` | `Failed` |
| Connection drop after DATA | — | `TransientError` | stays `Sending` |

`RetryStuckSendingAsync` picks up all `Status=Sending` messages at startup and retries them. `Failed` messages are not touched.

---

## Protocol

### Chat-* headers

```
Chat-Version: 2.0
Chat-Message-ID: <uuid>@localhost
Chat-Timestamp: 2026-04-11T10:00:00+03:00
Chat-Group-ID: <group-uuid>          # group messages only
Chat-Encryption: pgp-inline          # if encrypted
Chat-Reaction: 👍
Chat-Reaction-To: <target-msg-id>
Chat-Edit-Of: <target-msg-id>
Chat-Edit-Version: 2
Chat-Delete-Of: <target-msg-id>
Chat-Read-Of: id1,id2,id3
Chat-System-Type: group-create       # system message
Chat-Sync-Type: read-state           # cross-device sync
Chat-Invite-Token: <raw-token>       # invite messages only
Initial-Contact-Key-Exchange: <base64>  # encrypted pubKey (invite messages only)
Autocrypt: addr=user@example.com; keydata=<base64-pubkey>  # not in invite messages
In-Reply-To: <target-msg-id>
```

### Invite and key exchange

Token — 30 Base32 characters (`XXXXX-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX`), generated by `InviteService`.
Invite URL: `echat://invite?e={email}&n={name}&t={rawToken}`.

**First message (Bob → Alice):**

```
Chat-Invite-Token: <rawToken>
Initial-Contact-Key-Exchange: <base64(nonce[12] + tag[16] + AES-GCM(pubKeyBob))>
```

AES-256-GCM key = `SHA-256(Normalize(rawToken))`. Nonce is 12 random bytes. The Autocrypt header is **not added** to invite messages — the public key is never transmitted in plaintext.

**Alice on receipt:**

1. Takes `headers.EncryptedContactKey` and `headers.InviteToken`
2. `InviteService.DecryptPubKey(encrypted, token)` — decrypts Bob's public key
3. Only after successful decryption — `VerifyAndConsumeAsync()` (token is burned)
4. `contact.PublicKey = senderPubKey`, `contact.Verified = true`

The token is burned only on successful decryption — protects against replay without knowledge of the plaintext token.

### Encryption (pgp-inline)

**Unencrypted message**: all Chat-* headers on the outside, body = plaintext.

**Encrypted message**: only `Chat-Version`, `Chat-Group-ID`, `Autocrypt` as outer headers. Everything else is inside the PGP-encrypted body:

```
Chat-Message-ID: <uuid>
Chat-Timestamp: ...
<other metadata>
                           ← blank line separator
Message text

--echat-att--              ← attachment block (if any)
Content-Type: image/jpeg
Content-Filename: photo.jpg
Content-Size: 12345

<base64 data>
--echat-att-end--
```

**Important**: `email.Attachments` (MimeKit) only contains parts with `Content-Disposition: attachment` — for encrypted messages this is always empty. Attachments are encoded as text blocks inside the ciphertext.

### Batching

`BatchKey = {Recipients (HashSet), GroupId, Tier}`. Tiers:
- `Immediate` — sent immediately, no batching (all user-initiated messages)
- `System` — fast batch (system messages)
- `LowPriority` — slow batch (rate limit conservation)

---

## Database

**Engine**: SQLite with WAL mode (`PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;`).

**EF Core scoped context**: `ChatDbContext` is scoped, not singleton. Use `IServiceScopeFactory.CreateScope()` inside singletons:
```csharp
using var scope = _scopeFactory.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
```

**Atomic UnreadCount increment** — always via `ExecuteUpdateAsync`:
```csharp
await db.Chats
    .Where(c => c.ChatId == chatId)
    .ExecuteUpdateAsync(s => s.SetProperty(c => c.UnreadCount, c => c.UnreadCount + 1));
```

**DateTimeOffset and SQLite**: EF Core cannot translate `DateTimeOffset` comparisons into WHERE clauses on SQLite. Pattern: load rows into memory (`.ToListAsync()`), filter in C#. ORDER BY works fine.

---

## UI

### Platform split

`IPlatformService.IsDesktop` — the only branching mechanism for UI layout. On Windows — three-column layout (ChatList left, chat right). On Android — sequential navigation via Blazor Router.

`ChatList.razor` contains both layouts in a single file, switched via `@if (Platform.IsDesktop)`.

All `ChatList` logic is in the code-behind file `ChatList.razor.cs` (partial class).

### Components

| Component | Description |
|---|---|
| `MessageBubble` | Renders a single message. Used on both desktop and mobile |
| `ChatListItem` | Chat list row item |
| `ChatInfoModal` | Chat / group member info |
| `NewChatModal` | New chat modal (3 tabs: My Invite, Add Contact, New Group) |

### IPlatformService

Interface for platform-specific operations. Implementations: `PlatformService` (MAUI) and `WebPlatformService` (Web).

```csharp
bool IsDesktop                          // Windows = true, others = false
bool SupportsMauiFilePicker             // MAUI = true, Web = false
bool SupportsPickFolder                 // Android = true (SAF), others = false
bool SupportsBackgroundNotificationToggle // Android = true, others = false
bool SupportsInAppUpdate                // Windows + Android = true, iOS + Web = false

Task SaveFileAsync(...)                 // Native save dialog
Task<Stream?> PickFileAsync(...)        // Native file picker
Task OpenAttachmentAsync(...)           // Open file with native app
Task<bool> SaveToDownloadsAsync(...)    // Save to Downloads folder (Android/Windows)
Task<bool> SaveToPickedFolderAsync(...) // SAF dialog (Android)
void UpdateBadge(int totalUnread)       // Taskbar badge (Windows)
void RestartApp()                       // Restart the app
Task SetBackgroundNotificationVisibleAsync(bool) // BG notification (Android)
Task OpenBatteryOptimizationSettingsAsync()      // Battery dialog (Android)
Task ApplyUpdateAsync(url, version, onProgress)  // Download and install update
```

On Web all methods are no-ops returning `Task.CompletedTask` / `false`.

### In-app update

`UpdateService` (singleton in `EChat.Core`) checks the GitHub Releases API:

```
GET https://api.github.com/repos/rsvln/echat/releases/latest
```

Compares `tag_name` with `VersionInfo.AppVersion`. Result is cached for the session; `InvalidateCache()` forces a re-check.

**Windows flow**: download ZIP → extract to `%TEMP%\echat-update\` → write `update.bat` (robocopy + restart) → launch script → `Application.Quit()`.

**Android flow**: check `CanRequestPackageInstalls()` → if denied, open system settings → download APK to cache dir → `FileProvider` URI → install intent. The FileProvider authority is `{packageName}.update.provider`, declared in `AndroidManifest.xml` with `update_file_paths.xml`.

### Android back button

`AndroidBackHandler` (static class in `EChat.UI/Services/`):
- `ChatView.razor` registers a callback on init
- `ChatView.razor.Dispose()` unregisters it
- `MainActivity` uses `OnBackPressedDispatcher.AddCallback()` (not `override OnBackPressed()` — doesn't fire with gesture navigation on Android 13+)

### MessageBubble

- `GetAttachmentUrl(att)` — reads the file via `DbPathInfo.ResolveFilePath(att.FilePath)`, returns `data:{contentType};base64,...`
- **Lightbox**: opens on image tap. On mobile supports pinch-to-zoom (×1–×6), double-tap (toggle ×1/×2.5), single-finger pan. JS functions: `initLightboxZoom`, `isLightboxZoomed`, `resetLightboxZoom` in `index.html`
- Tap on background at zoom > 1 — resets zoom first; at zoom = 1 — closes
- `MobileMode` prop disables the context menu (mobile uses a top action bar instead)

### CSS

Global styles: `src/EChat.MAUI/wwwroot/css/app.css` and `src/EChat.UI/wwwroot/css/app.css`.

**Important**: inline `<style>` in components is only injected when the component renders. Shared styles (`.ctx-menu`, etc.) must live in `app.css`, otherwise they won't load in an empty chat.

---

## Multi-account

- One account has `IsActive=true` — handled by `EmailTransportService`
- All others run as background workers `AccountImapWorker` managed by `MultiAccountImapManager`
- On switch: `ChatEventService.NotifyAccountSwitched()` → workers restart

---

## Cross-device sync

When a message is sent, the sender adds themselves as CC. Another device on the same mailbox receives the email, parses it as `isSentSync=true` (sender == accountEmail), and saves it with status `Sent` without incrementing `UnreadCount`.

Deduplication is by `MessageId` (UUID@localhost). `IncomingMessageService` checks for the `MessageId` in the database before saving. No DeviceId is used.

`DeviceSyncService` sends sync messages (`Chat-Sync-Type: read-state`), which `IncomingMessageService` processes separately.

---

## Versioning

### Version files

Each project has its own `version.txt` (tracked in git):
```
src/EChat.Core/version.txt    # e.g. 0.2.17
src/EChat.UI/version.txt
src/EChat.MAUI/version.txt
src/EChat.Web/version.txt
```

The version is read in `.csproj` via MSBuild: `$([System.IO.File]::ReadAllText('version.txt'))`.
`InformationalVersion` is formed as `{version}+{yyyyMMddHHmm}` — every build is uniquely stamped.

`UpToDateCheckInput` in each `.csproj` ensures a rebuild when `version.txt` changes.

### Version display

`VersionInfo.VersionOverride` (static) — set by the host project at startup:
- `MauiProgram.cs` — from `AssemblyInformationalVersionAttribute` of the MAUI assembly
- `Web/Program.cs` — from `Assembly.GetExecutingAssembly()`

This ensures the About screen shows the host version, not `EChat.Core`'s.

`VersionInfo.BuildDate` parses the `yyyyMMddHHmm` suffix and formats it as `"20260424|1523"`.

### bump-versions.ps1

`scripts/bump-versions.ps1` — smart increment before publishing:

```powershell
# Modes:
bump-versions.ps1            # all: Core/UI if changed + MAUI + Web
bump-versions.ps1 -Mode win  # Core/UI if changed + MAUI
bump-versions.ps1 -Mode web  # Core/UI if changed + Web
bump-versions.ps1 -Diagnose  # Show changed files without bumping
```

Change detection: SHA-256 hash of all `.cs`, `.razor`, `.csproj` files in the project (excluding `bin/` and `obj/`) is stored in `.src-hash` next to the project. On the next run the hashes are compared.

`.src-hash` is in `.gitignore`.

---

## Android — Background operation

### EmailSyncService

Foreground service (`ForegroundService.TypeDataSync`). Started from `App.xaml.cs` after `ReconnectAsync`. Keeps the process alive until Android forcefully kills it.

`StartCommandResult.Sticky` — Android restarts the service after a kill. On restart, `IPlatformApplication.Current` may be `null` while MAUI initialises. The service waits up to 10 seconds with retries.

**Notification visibility**: after the mandatory `StartForeground()` call, `bg_notification_visible` is read from `IAppPreferences`. If `false`, `StopForeground(StopForegroundFlags.Remove)` is called immediately. The service continues running; only the notification is removed.

### Activity lifecycle

`MainActivity` has a static flag `_processProperlyStarted`. When the Activity is restored after an Android process kill (`savedInstanceState != null && !_processProperlyStarted`), the Activity restarts cleanly via `StartActivity + Finish`. This prevents a blank screen: the Blazor WebView cannot restore state from `savedInstanceState`.

### Global exception handling

`MainApplication.cs` registers:
- `TaskScheduler.UnobservedTaskException` — `SetObserved()` + log to `Android.Util.Log`. Without this, unhandled exceptions in `Task.Run` kill the process during GC
- `AppDomain.CurrentDomain.UnhandledException` — log before crash

---

## Logging

`FileLogger` — singleton. Levels: `None | Error | Warn | Info | Debug`. Configurable in the Settings UI.

```csharp
_fileLogger.Write("INFO", "MyService", $"Something happened: {detail}");
```

On Android additionally: `Android.Util.Log.Debug/Error("eChat", message)` — visible in logcat.

---

## Build and publish

### Development

```bash
# Windows desktop
dotnet run --project src/EChat.MAUI -f net10.0-windows10.0.19041.0

# Android (requires emulator or device)
dotnet build src/EChat.MAUI -f net10.0-android -c Release -t:SignAndroidPackage
```

### Publishing

```bat
publish.bat       # All platforms: Core/UI (if changed) + MAUI + Web
publish-win.bat   # Windows only: Core/UI (if changed) + MAUI
```

`publish.bat` does:
1. `scripts/bump-versions.ps1` — increments versions
2. Builds Windows desktop → `pub/win/`, creates `pub/distr/EChat-win.zip`
3. Builds Android APK → `pub/distr/EChat.apk`
4. Builds and pushes the Docker image to GHCR, generates `pub/distr/docker-compose.yml`
5. Copies distributables to `e:\YandexDisk\share\echat\`

`pub/` and `.claude/` directories are excluded from git via `.gitignore`.

### GitHub Actions

On every push to `master`:
1. Reads the version from `src/EChat.MAUI/version.txt`
2. If the tag `v{version}` already exists — skips the build
3. If new — builds Windows ZIP, Android APK, and Docker image in parallel
4. Creates the git tag, uploads artifacts to a GitHub Release, pushes the Docker image to GHCR (`ghcr.io/rsvln/echatweb`)

### Docker (EChat.Web)

`Dockerfile` (multi-stage, Alpine):
- Copies `*.csproj` **and** `version.txt` for each project before `dotnet restore` (MSBuild reads `version.txt` when parsing `.csproj`)
- Data dir: `ECHAT_DATA_DIR` env var → `EChat:DataDir` config → `{ContentRoot}/data`
- No SSL — termination is expected at nginx/Traefik

---

## Credential security

### ICredentialProtector

Interface for encrypting/decrypting sensitive fields (IMAP password, private PGP key) in SQLite.

```csharp
string Protect(string plaintext)      // idempotent: already-encrypted values returned as-is
string Unprotect(string ciphertext)   // legacy plaintext (no prefix) returned as-is
bool IsProtected(string storedValue)  // true if it carries a platform prefix
```

Implementations:
- `DpapiCredentialProtector` (Windows) — prefix `dpapi:`, entropy `"echat-cred-v1"`
- `SecureStorageCredentialProtector` (Android) — prefix `aes:`, AES-256-GCM via Android Keystore
- `PlaintextCredentialProtector` — no-op, for Web and development

**Startup step**: reads raw values from the database via a direct `SqliteConnection` (bypassing EF Value Converters), checks `IsProtected()`. If all values are already encrypted — `SaveChanges` is not called.

---

## Groups — DisplayName protocol

`group-create`, `group-member-add`, `group-member-remove` messages carry member names:

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

`IncomingMessageService` saves `DisplayName` in `GroupMember` on creation; updates it if a better name arrives.

Fallback chain for display: `GroupMember.DisplayName` → `Contact.DisplayName` → full email (never `email.Split('@')[0]`).

---

## NTP time sync

`NtpTimeService` corrects `DateTimeOffset.UtcNow` via an atomic `_offsetTicks`.

**Important**: `socket.ReceiveTimeout` is ignored by `await ReceiveAsync`. The timeout is implemented via `CancellationTokenSource(5s)` passed to `ConnectAsync` / `SendAsync` / `ReceiveAsync`.

NTP response validation:
1. `received < 48` → `InvalidDataException`
2. `intPart == 0` → `InvalidDataException` (zero timestamp = garbage)
3. `|networkDateTime - UtcNow| > 3650 days` → `InvalidDataException` (sanity check)

On NTP failure — HTTP HEAD fallback to the mail domains of connected accounts (from the `Date` response header).

---

## Invariants and pitfalls

1. **Never use `Environment.SpecialFolder.LocalApplicationData` for file paths** — returns the internal storage path on Android. Use `_fileLogger.AppDir` or `DatabasePathInfo.AttachmentsDir`.

2. **`DatabasePathInfo.ResolveFilePath()`** — always use this method to get the absolute path of an attachment. Handles both legacy (absolute) paths and new (relative filename) records.

3. **`DateTimeOffset` in EF Core + SQLite WHERE clauses** — does not work. Load rows, filter in C#.

4. **`email.Attachments` (MimeKit) for encrypted messages** — returns an empty list. Attachments are encoded in `--echat-att--` blocks inside the ciphertext.

5. **`OnBackPressed()` override on Android 13+** — does not fire with gesture navigation. Use only `OnBackPressedDispatcher.AddCallback()`.

6. **Inline `<style>` in components** — only injected when the component renders. If the component doesn't render (empty chat), styles don't load. Shared styles go only in `app.css`.

7. **`MessageId` for deduplication** — globally unique (UUID@localhost). Checked in `IncomingMessageService` before saving.

8. **`UnreadCount`** — incremented only when `!isSentSync`. Do not update directly via EF tracking — use `ExecuteUpdateAsync` for atomicity.

9. **Tombstone on chat delete** — set `Deleted=true`, do not delete the `Chat` row. Otherwise `group-create` from an IMAP re-sync will recreate the chat.

10. **Group encryption** — on `group-create` the group's private key is sent to each member individually (encrypted with their public key). Subsequent messages are encrypted with the group's public key.

11. **`BatchTier.Immediate`** — the only tier with no delay. All user-initiated messages use `Immediate`.

12. **Optimistic UI on send with attachment** — after writing the file to disk and before `StateHasChanged()`, add attachment entities to `_messageAttachments[msgId]`. Otherwise the image won't appear until restart (applies to both `ChatList.razor.cs` and `ChatView.razor`).

13. **`version.txt` in Dockerfile** — must be copied alongside `.csproj` before `dotnet restore`. MSBuild computes `<Version>` from `version.txt` when parsing `.csproj`, not at compile time.

14. **`TaskScheduler.UnobservedTaskException` on Android** — without `SetObserved()`, an unhandled exception in `Task.Run` eventually kills the process during GC. Always register in `MainApplication`.

15. **`PRAGMA foreign_keys` inside an EF transaction** — SQLite silently ignores `PRAGMA foreign_keys = OFF/ON` inside an active transaction. For migrations that need to reconstruct tables with FKs, use `migrationBuilder.Sql("PRAGMA ...", suppressTransaction: true)` — EF commits the transaction before execution.

16. **`email.Split('@')[0]` — never** — the email prefix is not a user name. Use `DisplayName ?? full_email` for display. The `Split('@')[0]` fallback has been removed from the entire codebase.

17. **EF Value Converter does not fire on raw `SELECT`** — converters (e.g. credential encryption) are only applied when writing through EF. Reading raw values to check `IsProtected()` requires a direct `SqliteConnection`, not a `DbContext`.

18. **`GroupMember.DisplayName` — source of truth for group member names** — do not look up `Contacts` when you need a group member's name. The contact may not be known (added by another member). `GroupMember.DisplayName` is populated from the protocol and is always up to date.
