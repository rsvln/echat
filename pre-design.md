вот кстати типа айдл и приоритеты тиеров с паузами хорошо бы вынести в тонкие настройки приложухи. а может кому то неважно прям в онлайне получать и он уберет айдл и выставит себе раз в минуту инициировать соединение. что думаешь?


Абсолютно! Это прям в духе decentralized/self-hosted софта - дать пользователю полный контроль. Каждый сам решает баланс между real-time и ресурсами.
Профили синхронизации


ppublic enum SyncProfile
{
    Realtime,      // IMAP IDLE + instant send
    Balanced,      // IDLE днем, polling ночью
    PowerSaver,    // polling каждые 5-15 мин
    Manual,        // только по запросу пользователя
    Custom         // детальная настройка
}

public class SyncSettings
{
    // Transport
    public bool UseImapIdle { get; set; } = true;
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMinutes(5);
    
    // Batching tiers
    public TimeSpan ImmediateBatchWindow { get; set; } = TimeSpan.Zero;
    public TimeSpan SystemBatchWindow { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan LowPriorityBatchWindow { get; set; } = TimeSpan.FromSeconds(60);
    
    // Network
    public bool AllowCellularSync { get; set; } = true;
    public bool SyncOnMeteredConnection { get; set; } = false;
    
    // Schedule
    public TimeRange QuietHours { get; set; } = new(22, 8); // 22:00-8:00
    public SyncProfile QuietHoursProfile { get; set; } = SyncProfile.PowerSaver;
}
```

## UI для настроек

**Простой режим (пресеты):**
```
┌─────────────────────────────────────┐
│ Sync mode:                          │
│                                     │
│ ○ Real-time (battery drain)        │
│   IMAP IDLE, instant delivery       │
│                                     │
│ ● Balanced (recommended)            │
│   Real-time during day,             │
│   every 15 min at night             │
│                                     │
│ ○ Power Saver                       │
│   Check every 5-15 minutes          │
│                                     │
│ ○ Manual                            │
│   Only when I open the app          │
│                                     │
│ ○ Custom...                         │
└─────────────────────────────────────┘
```

**Продвинутый режим:**
```
┌─────────────────────────────────────┐
│ Advanced Sync Settings              │
├─────────────────────────────────────┤
│                                     │
│ Transport:                          │
│ ☑ Use IMAP IDLE when available      │
│ Polling interval: [5] minutes       │
│                                     │
│ Batching windows:                   │
│ User messages: [0] seconds          │
│ System events:  [10] seconds        │
│ Read receipts:  [60] seconds        │
│                                     │
│ Network:                            │
│ ☑ Sync on cellular                  │
│ ☐ Sync on metered connections       │
│                                     │
│ Quiet hours: 22:00 - 08:00          │
│ During quiet hours: [Every 30 min ▼]│
└─────────────────────────────────────┘
Адаптивная логика
csharppublic class AdaptiveSyncEngine
{
    private readonly SyncSettings _settings;
    private readonly BatteryMonitor _battery;
    private readonly NetworkMonitor _network;
    
    public async Task<SyncStrategy> GetCurrentStrategy()
    {
        // Override для низкого заряда
        if (_battery.Level < 15)
        {
            return new SyncStrategy
            {
                UseIdle = false,
                PollingInterval = TimeSpan.FromMinutes(15),
                Reason = "Low battery"
            };
        }
        
        // Quiet hours
        if (IsQuietHours())
        {
            return ApplyProfile(_settings.QuietHoursProfile);
        }
        
        // Metered connection
        if (_network.IsMetered && !_settings.SyncOnMeteredConnection)
        {
            return new SyncStrategy
            {
                UseIdle = false,
                PollingInterval = TimeSpan.FromMinutes(30),
                Reason = "Metered connection"
            };
        }
        
        // Обычные настройки
        return new SyncStrategy
        {
            UseIdle = _settings.UseImapIdle,
            PollingInterval = _settings.PollingInterval
        };
    }
}
```

## Статистика для пользователя
```
┌─────────────────────────────────────┐
│ Sync Statistics (last 24h)          │
├─────────────────────────────────────┤
│ Messages synced: 142                │
│ Background wakeups: 23              │
│ Battery used: ~2.3%                 │
│ Data transferred: 1.2 MB            │
│                                     │
│ Avg delivery delay: 12 seconds      │
│                                     │
│ [Optimize for battery]              │
│ [Optimize for real-time]            │
└─────────────────────────────────────┘
Клиент собирает метрики и может предложить: "Вы проверяете почту раз в час, но IDLE включен - выключить для экономии батареи?"
Per-chat настройки
Важные чаты = real-time, остальные = relaxed.
csharppublic class ChatSyncPriority
{
    public enum Priority
    {
        High,      // IDLE always, instant notifications
        Normal,    // Follow global settings
        Low,       // Lazy sync, batch everything
        Muted      // Sync only on manual refresh
    }
    
    public Dictionary<string, Priority> ChatPriorities { get; set; }
}
```

UI:
```
Long press на чат → Sync priority:
  • High (real-time)
  • Normal
  • Low (save battery)
  • Muted
Feedback для пользователя
Если кто-то выбрал очень редкий sync:
csharppublic class SyncWarningService
{
    public void ValidateSettings(SyncSettings settings)
    {
        if (!settings.UseImapIdle && settings.PollingInterval > TimeSpan.FromMinutes(30))
        {
            ShowWarning(
                "Messages may be delayed by up to {settings.PollingInterval}. " +
                "Consider enabling IMAP IDLE for important chats."
            );
        }
        
        if (settings.LowPriorityBatchWindow > TimeSpan.FromMinutes(10))
        {
            ShowInfo(
                "Read receipts will be sent with up to {settings.LowPriorityBatchWindow} delay."
            );
        }
    }
}
Интеграция с системными настройками
Android:
csharp#if ANDROID
var powerManager = Platform.CurrentActivity.GetSystemService(Context.PowerService) as PowerManager;
if (powerManager.IsPowerSaveMode)
{
    // Автоматом переключаемся в PowerSaver profile
    _syncEngine.OverrideProfile(SyncProfile.PowerSaver);
}
#endif
iOS:
csharp#if IOS
NSNotificationCenter.DefaultCenter.AddObserver(
    UIDevice.BatteryStateDidChangeNotification,
    notification => {
        if (UIDevice.CurrentDevice.BatteryState == UIDeviceBatteryState.Unplugged)
        {
            _syncEngine.AdaptToLowPower();
        }
    }
);
#endif
Экспорт/импорт настроек
json{
  "sync_profile": "custom",
  "imap_idle": false,
  "polling_interval_minutes": 10,
  "batch_windows": {
    "immediate": 0,
    "system": 15,
    "low_priority": 120
  },
  "quiet_hours": {
    "enabled": true,
    "start": "22:00",
    "end": "08:00",
    "profile": "power_saver"
  }
}


# Delta Chat .NET - Technical Specification

## Цель проекта

Email-based мессенджер на .NET с улучшениями над оригинальным Delta Chat:
- Батчинг сообщений для снижения нагрузки на email серверы
- Гибкие настройки синхронизации (от real-time до manual)
- Правильная обработка порядка сообщений через NTP
- Multi-device sync
- Blazor Hybrid UI для Android/Windows/Desktop

## Архитектура

### Слои приложения
```
DeltaChat.Core/
  ├─ Protocol/           # Парсинг/генерация email headers
  ├─ Transport/          # IMAP/SMTP через MailKit
  ├─ Crypto/             # PGP через BouncyCastle
  ├─ Storage/            # SQLite + EF Core
  └─ Sync/               # Логика синхронизации

DeltaChat.UI/
  ├─ Pages/              # Blazor компоненты
  ├─ Components/
  └─ Services/           # UI-специфичные сервисы

DeltaChat.Maui/
  ├─ Platforms/
  │   ├─ Android/        # Background services, notifications
  │   ├─ Windows/
  │   └─ iOS/            # (опционально)
  └─ MauiProgram.cs
```

### Ключевые компоненты

#### 1. Protocol Layer

**ChatMessageParser**
- Входные данные: `MimeMessage` из MailKit
- Извлечение custom headers: `Chat-Version`, `Chat-Message-ID`, `Chat-Timestamp`, и т.д.
- Обработка батчей: если `Chat-Batch: true`, парсим `multipart/mixed` с вложенными сообщениями
- Валидация структуры сообщения
- Возврат: `List<ChatMessage>` (один или несколько если батч)

**ChatMessageBuilder**
- Создание `MimeMessage` с правильными headers
- Поддержка single/batch режимов
- Autocrypt headers для key exchange
- Group metadata (members, admins, version)

#### 2. Transport Layer

**EmailTransportService**
- IMAP listener с IDLE support
- SMTP отправка через MailKit
- Retry logic с exponential backoff
- Rate limiting detection (SmtpCommandException handling)
- Connection pooling для SMTP

**BatchQueue**
- Очередь исходящих сообщений с группировкой по `BatchKey`
- `BatchKey`: Recipients hash + GroupId + Tier
- Три tier'а: Immediate (0s), System (10s), LowPriority (60s)
- Timer-based flush каждые N секунд
- Адаптивное окно батчинга в зависимости от активности чата
- При ошибке отправки батча - разбиение на отдельные сообщения

**MessageDeduplicator**
- Проблема: групповое сообщение попадает в Sent и в каждую INBOX копию
- Решение: хеширование `Chat-Message-ID` + sender + timestamp
- Хранение хешей последних N сообщений в памяти (LRU cache)
- Проверка перед добавлением в БД

#### 3. Sync Strategy

**SyncEngine**
- Определение текущей стратегии на основе:
  - Пользовательских настроек (SyncProfile)
  - Уровня батареи
  - Типа сети (WiFi/cellular/metered)
  - Quiet hours
- Переключение между IMAP IDLE и polling
- Adaptive batch windows

**Profiles:**
- `Realtime`: IDLE + instant send
- `Balanced`: IDLE днем, polling ночью
- `PowerSaver`: polling каждые 5-15 мин
- `Manual`: только при открытии приложения
- `Custom`: детальные настройки

#### 4. Time Service

**NtpTimeService**
- Синхронизация с NTP при старте приложения
- Периодическая ресинхронизация (раз в час)
- Хранение offset между system time и NTP time
- Fallback на system time если NTP недоступен
- `GetAccurateTime()` возвращает скорректированное время

**MessageOrderCorrector**
- Causal ordering: если reply раньше parent по timestamp - коррекция
- Хранение двух timestamps: оригинальный + display (скорректированный)
- Сортировка по display timestamp в UI

#### 5. Group Management

**GroupStateManager**
- Хранение state группы: members, admins, version, name
- Union merge при конфликтах версий
- Валидация прав: только админы могут add/remove admins
- Генерация Group Operation headers

**Tables:**
```sql
chat_groups: group_id, name, version, created_at
group_members: group_id, member_email, role, added_at, added_by
group_operations: id, group_id, version, operation, actor, target, timestamp
```

**Conflict resolution:**
- Если одинаковая version у двух разных states → union merge
- Members: объединение множеств
- Admins: только если оба изменения от админов, иначе игнорируем non-admin changes
- Версия инкрементится после merge
- Name: выбираем по timestamp

#### 6. Crypto

**PgpService**
- Генерация ключевых пар (RSA 4096 или ECC)
- Encrypt/Decrypt через BouncyCastle
- Autocrypt header generation/parsing
- Key storage в БД с fingerprints

**KeyVerificationService**
- QR код генерация/сканирование (ZXing.Net)
- Temporary link generation (опционально, требует хостинга)
- Fingerprint comparison UI
- Verified contacts flag в БД

#### 7. Multi-Device Sync

**DeviceSyncService**
- Отправка self-emails в папку `.DeltaChat-Sync`
- Типы sync: read-state, drafts, settings, muted-chats
- Батчинг sync messages (не спамить себе)
- Парсинг sync messages от других устройств
- Last-Write-Wins конфликт резолюшен по timestamp + device-id
- Auto-cleanup sync messages старше 7 дней

#### 8. Storage

**ChatDbContext (EF Core)**
```
Messages: message_id, chat_id, sender, content, timestamp, display_timestamp, 
          encrypted, attachment_path, in_reply_to
          
Chats: chat_id, type (1-1/group), name, last_message_id, 
       unread_count, muted, archived
       
Contacts: email, display_name, public_key, key_fingerprint, 
          verified, last_seen
          
Settings: key, value (JSON для сложных объектов)
```

**Indexes:**
- `messages(chat_id, timestamp)` для быстрой выборки
- `messages(message_id)` уникальный
- `group_members(group_id)` для списка участников

#### 9. Attachments

**AttachmentManager**
- Проверка размера относительно `EmailAccountConfig.MaxAttachmentSizeMb`
- Auto-detection лимитов для известных провайдеров (Gmail=25MB, Outlook=150MB, etc)
- Предупреждение пользователя если файл слишком большой
- Хранение attachments в `FileSystem.AppDataDirectory/attachments/`
- Cleanup неиспользуемых файлов

## Протокол: Custom Headers

### Базовые
```
Chat-Version: 1.0
Chat-Message-ID: <uuid@sender-domain>
Chat-Timestamp: 2026-03-27T15:30:45.123Z
In-Reply-To: <parent-message-id>
```

### Батчинг
```
Chat-Batch: true
Chat-Batch-Count: 3
Chat-Batch-Tier: system
Chat-Batch-Item-Index: 0
```

Structure: `multipart/mixed` с вложенными `message/rfc822`

### Группы
```
Chat-Group-ID: <uuid@domain>
Chat-Group-Version: 7
Chat-Group-Name: Проект X
Chat-Group-Members: alice@ex.com,bob@ex.com,charlie@ex.com
Chat-Group-Admins: alice@ex.com
Chat-Group-Operation: member-add
Chat-Group-Operation-Actor: alice@ex.com
Chat-Group-Operation-Target: dave@ex.com
```

### Encryption
```
Chat-Encryption: openpgp
Autocrypt: addr=alice@ex.com; keydata=<base64>
Autocrypt-Gossip: addr=bob@ex.com; keydata=<base64>
```

### System Messages
```
Chat-Disposition: read-notification
Chat-Disposition-ID: <original-message-id>

Chat-Reaction: 👍
Chat-Reaction-To: <target-message-id>

Chat-Edit-Of: <original-message-id>
Chat-Edit-Version: 2
```

### Multi-Device Sync
```
Chat-Sync-Type: read-state | draft | settings
Chat-Sync-Device-ID: desktop-abc123
Chat-Sync-Version: 1
```

Body: JSON payload с данными для синхронизации

## Platform-Specific

### Android

**ForegroundService для background sync:**
- Запуск при старте приложения если enabled IDLE
- Foreground notification "Syncing messages..."
- Handling Doze mode: fallback на WorkManager
- Battery optimization permissions

**Notifications:**
- Группировка по чатам
- Quick reply action
- Mark as read action
- Notification channels для разных приоритетов

### Windows

**Background Task:**
- Обычный .NET background service без ограничений
- Tray icon с индикатором новых сообщений
- Toast notifications

### Общее

**Permissions:**
- Network access
- Storage (для attachments)
- Notifications
- Battery optimization exemption (Android, опционально)

## Configuration & Settings

**User-facing settings:**
```
Sync Mode: [Realtime | Balanced | PowerSaver | Manual | Custom]

Custom settings:
- Use IMAP IDLE: [x]
- Polling interval: [5] minutes
- Batch window (user messages): [0] sec
- Batch window (system): [10] sec  
- Batch window (low priority): [60] sec
- Sync on cellular: [x]
- Sync on metered: [ ]
- Quiet hours: 22:00 - 08:00
- Quiet hours profile: [PowerSaver]

Per-chat priority:
- High (real-time)
- Normal (follow global)
- Low (save battery)
- Muted (manual only)
```

**EmailAccountConfig:**
```
IMAP/SMTP server autodiscovery (Thunderbird autoconfig XML)
Max attachment size detection
Credentials storage (platform keychain)
```

## Backwards Compatibility

**Delta Chat compatibility mode:**
- Детектирование по `Chat-Version: 1.0` без `-batching` суффикса
- Отключение батчинга при общении с Delta Chat клиентами
- Поддержка их headers в полном объеме
- Наши расширения игнорируются старыми клиентами (graceful degradation)

**Version detection:**
```
Chat-Version: 1.0              # Original Delta Chat
Chat-Version: 2.0-batching     # Наша версия с батчингом
```

При получении сообщения от контакта сохраняем его версию протокола в БД.
При отправке используем наименьший общий знаменатель.

## Edge Cases & Error Handling

1. **SMTP rate limiting:** увеличение batch window, exponential backoff
2. **IMAP IDLE timeout:** reconnect с задержкой
3. **Offline message queue:** persist в БД, отправка при появлении сети
4. **Corrupted messages:** skip с логированием, не падаем
5. **Clock skew > 5 минут:** warning в UI, NTP resync
6. **Encryption key missing:** показываем как unencrypted с предупреждением
7. **Group version conflicts:** union merge + log warning
8. **Duplicate messages:** hash-based deduplication
9. **Partial batch delivery:** применяем успешные items, retry failed
10. **Self-hosted email server SSL issues:** allow custom CA certificates

## Performance Targets

- **Startup time:** <2 секунды на mid-range Android
- **Message send latency:** <500ms (local queue)
- **UI render 1000 messages:** <100ms (virtualized list)
- **Database query time:** <50ms для любого запроса
- **Memory footprint:** <150MB на Android
- **Battery drain:** <2% за 24 часа при умеренном использовании

## Testing Strategy

**Unit tests:**
- Protocol parsing/generation
- Batch merge logic
- Group conflict resolution
- Time service с мокированным NTP
- Deduplication logic

**Integration tests:**
- IMAP/SMTP через test email account
- Батчинг end-to-end
- Multi-device sync
- Encryption roundtrip

**Manual testing:**
- Разные email providers (Gmail, Outlook, self-hosted)
- Battery drain на реальных устройствах
- Network interruptions
- Concurrent group modifications

## MVP Scope

**Обязательно:**
- [x] 1-1 чаты
- [x] Групповые чаты с admin system
- [x] PGP encryption через Autocrypt
- [x] Батчинг с тремя tiers
- [x] Настройки sync profiles
- [x] NTP time sync
- [x] Message deduplication
- [x] Attachments с size limits
- [x] Multi-device sync (read states)
- [x] Android + Windows support

**Можно позже:**
- [ ] iOS support
- [ ] Voice messages
- [ ] Video calls signaling
- [ ] Advanced formatting (только markdown в MVP)
- [ ] Message search (full-text)
- [ ] Import/export chats
- [ ] Backup/restore

## Не делаем

- ❌ Typing indicators
- ❌ Online status
- ❌ Stories/статусы
- ❌ Channels/боты
- ❌ Стикеры
- ❌ Custom themes (одна светлая, одна темная тема)
- ❌ Chat folders/labels
- ❌ Scheduled messages

## Tech Stack

- **.NET 10** (latest LTS)
- **MAUI Blazor Hybrid** для UI
- **MailKit** для IMAP/SMTP
- **Portable.BouncyCastle** для PGP
- **EF Core + SQLite** для storage
- **ZXing.Net** для QR codes
- **NTP client** для time sync

## Deployment

**Android:**
- APK build через GitHub Actions
- F-Droid repository (опционально)
- Google Play (после полировки)

**Windows:**
- MSIX package
- Portable ZIP
- Возможно Microsoft Store

**Code signing:**
- Self-signed для alpha/beta
- Proper certificates для production

## Roadmap

**Phase 1 (MVP):** Core protocol + basic UI + Android
**Phase 2:** Windows desktop + multi-device sync polish
**Phase 3:** iOS + advanced features
**Phase 4:** Federation testing с Delta Chat сообществом