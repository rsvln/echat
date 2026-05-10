# εChat

A messenger built on top of ordinary email. No servers, no registration — just your own mailboxes.

## How it works

εChat uses your regular email account as a transport layer. Messages are sent as emails via SMTP and received via IMAP. To any outside observer it's a normal email with subject `[eChat]`, but εChat displays them as a chat. End-to-end PGP encryption is optional and automatic.

Nothing is stored on third-party servers. All data lives in a SQLite database on your device.

## Features

- Direct and group chats
- End-to-end PGP encryption (keys generated automatically, exchanged via encrypted invite flow)
- Attachments: photos and files
- Reply, edit, delete messages
- Emoji reactions
- Multi-account (multiple mailboxes)
- Cross-device synchronisation
- Read receipts
- Chat search
- Archive and mute chats
- Contact management: block, notes, rename
- Context menu (right-click on desktop)
- Sync profiles: Real-time / Balanced / PowerSaver / Manual
- Quiet Hours with a dedicated sync profile
- Image viewer with pinch-to-zoom and double-tap (mobile)
- Backup and restore with encrypted backup support
- In-app update (Windows and Android)

## Platforms

| Platform | Status |
|---|---|
| Windows 10/11 | ✅ Ready |
| Android 7.0+ | ✅ Ready |
| Web (Docker) | ✅ Ready |
| iOS | 🔜 Planned |

## Installation

### Windows

1. Download `EChat-win.zip` from the Releases page
2. Extract and run `echat.exe`

### Android

Download `EChat.apk`, allow installation from unknown sources, install.

### Web / Docker

```bash
docker run -d \
  --name echat \
  --restart unless-stopped \
  -p 9999:8080 \
  -v /your/data/path:/app/data \
  ghcr.io/rsvln/echatweb:latest
```

Or use the `docker-compose.yml` from the Releases page. Open `http://your-server:9999`.

## Quick start

1. Open the app → tap **+** (add account)
2. Enter your email and password (for Gmail use an App Password)
3. IMAP/SMTP servers are detected automatically for popular providers
4. Tap **Save** — the app connects
5. Tap **+** → **My Invite** tab: copy your invite code and share it with a contact
6. Your contact enters the code in the **Add Contact** tab — εChat exchanges keys and creates the chat

Your first message arrives as a normal email. If the recipient uses εChat they see it in the chat. If not, they can reply by regular email.

## Supported providers

Auto-detection is available for:

- Gmail (requires App Password + IMAP enabled)
- Yandex Mail
- Mail.ru
- Outlook / Hotmail / Live
- iCloud (requires App Password)
- Any provider via manual server entry

## Android-specific settings (Settings → Appearance)

- **BG notify** — show or hide the persistent "Running in background" notification in the status bar. Background sync is unaffected — only the notification visibility changes
- **Battery** — opens the system dialog to disable battery optimisation for εChat (recommended for reliable background operation)

## Data and privacy

- No εChat server — everything is stored locally on your device
- Database: SQLite file on device (Windows: `%LocalAppData%\echat\`, Android: app external storage)
- Logs: `log/` folder next to the database, up to 20 files × 5 MB each, level configurable in Settings
- PGP keys are generated automatically and stored in the database
- Key exchange during first contact uses an AES-256-GCM encrypted channel (key = SHA-256 of the invite token) — the public key is never transmitted in plaintext
- Credentials (IMAP passwords, private PGP keys) are protected by DPAPI (Windows) or Android Keystore
- Backup: Settings → Backup (encrypted backups supported)

## Known limitations

- Delivery speed depends on the provider (typically seconds with IMAP IDLE)
- Public provider sending limits apply: ~500 emails/day for Gmail. Messages that exceed the limit are retried automatically on the next launch
- Group chats require all participants to use εChat
