# εChat (Epsilon Chat)

Email-мессенджер на .NET MAUI Blazor Hybrid. Работает поверх обычных почтовых аккаунтов через IMAP/SMTP — никаких серверов, никаких централизованных данных. Идеологически близок к Delta Chat, но с расширенной системой батчинга и мультиаккаунтностью.

---

## Особенности

- **Стандартный email** — работает с любым IMAP/SMTP почтовым сервером
- **Мультиаккаунт** — несколько почтовых аккаунтов в одном приложении, быстрое переключение
- **Горячее обновление настроек** — смена сервера/пароля без перезапуска приложения
- **Умный батчинг** — группировка исходящих сообщений снижает нагрузку на почтовый сервер и экономит батарею
- **Адаптивный UI** — трёхколоночный десктопный вид на Windows, компактный мобильный на Android
- **PGP/Autocrypt** — сквозное шифрование через обмен ключами в заголовках писем
- **Multi-device sync** — синхронизация состояния (прочитанность, черновики) между устройствами через специальную IMAP-папку
- **NTP-коррекция порядка** — сообщения всегда отображаются в правильном порядке даже при расхождении часов

---

## Архитектура

```
echat.sln
└── src/
    ├── EChat.Core/              # Бизнес-логика, без зависимости от MAUI
    │   ├── AccountConfig.cs     # Мутабельный синглтон: email + deviceId для горячей смены аккаунта
    │   ├── Models/              # Account, Chat, ChatMessage, Contact, GroupOperation, ...
    │   ├── Data/                # EF Core + SQLite (ChatDbContext)
    │   ├── Protocol/            # Парсинг/генерация custom email-заголовков
    │   │   ├── ChatMessageParser.cs
    │   │   ├── ChatMessageBuilder.cs
    │   │   ├── MessageDeduplicator.cs
    │   │   └── MessageOrderCorrector.cs
    │   ├── Transport/           # IMAP/SMTP через MailKit
    │   │   ├── ImapService.cs
    │   │   ├── SmtpService.cs
    │   │   ├── EmailTransportService.cs   # Оркестратор, ReconnectAsync
    │   │   └── BatchQueue.cs              # Очередь с группировкой по BatchKey
    │   ├── Sync/
    │   │   ├── SyncEngine.cs              # Выбор стратегии (IDLE vs polling)
    │   │   ├── NtpTimeService.cs          # Синхронизация времени
    │   │   └── DeviceSyncService.cs       # Межустройственная синхронизация
    │   └── Groups/
    │       └── GroupState.cs              # Состояние группового чата
    │
    ├── EChat.UI/                # Razor Class Library (без MAUI-зависимостей)
    │   ├── Pages/
    │   │   ├── Index.razor              # Стартовый экран: редирект на /setup или /chats
    │   │   ├── AccountSetup.razor       # Онбординг нового аккаунта
    │   │   ├── ChatList.razor           # Список чатов (десктоп + мобайл)
    │   │   ├── ChatView.razor           # Просмотр чата (мобайл)
    │   │   ├── AccountSettings.razor    # Настройки аккаунта
    │   │   └── Settings.razor           # Глобальные настройки приложения
    │   ├── Components/
    │   │   ├── MessageBubble.razor
    │   │   ├── ChatInfoModal.razor
    │   │   └── ChatListItem.razor
    │   └── Services/
    │       ├── UserContextService.cs    # Текущий аккаунт и email для UI
    │       ├── IPlatformService.cs      # Абстракция: IsDesktop
    │       └── IAppPreferences.cs       # Абстракция: Get/Set preferences
    │
    └── EChat.Maui/              # MAUI-хост: точка входа, платформенные реализации
        ├── MauiProgram.cs
        ├── App.xaml.cs          # Инициализация БД до старта Blazor
        ├── Services/
        │   ├── PlatformService.cs       # IsDesktop через #if WINDOWS
        │   └── AppPreferences.cs        # Maui.Storage.Preferences
        └── wwwroot/
            ├── index.html
            ├── favicon.svg
            └── css/app.css
```

---

## Протокол

Сообщения — обычные письма с дополнительными заголовками:

```
Chat-Version: 2.0-batching
Chat-Message-ID: <uuid@sender>
Chat-Timestamp: 2026-03-29T10:00:00Z
Chat-Group-ID: <uuid>           # только для групп
Chat-Batch: true                # батч-письмо
Chat-Sync-Type: read-state      # межустройственный sync
Autocrypt: addr=...; keydata=.. # обмен PGP-ключами
```

Батч-письма используют `multipart/mixed` с вложенными `message/rfc822`.

**Delta Chat совместимость:** сообщения с `Chat-Version: 1.0` обрабатываются в режиме совместимости без батчинга.

---

## Профили синхронизации

| Профиль | Стратегия |
|---|---|
| Realtime | IMAP IDLE, мгновенная доставка |
| Balanced | IDLE + адаптивный батчинг (рекомендуется) |
| Power Saver | Polling каждые 15 мин |
| Manual | Только вручную |
| Custom | Тонкая настройка |

Quiet Hours задаются отдельно для каждого аккаунта — в тихие часы автоматически применяется Power Saver или Manual.

---

## Технологии

| | |
|---|---|
| .NET 10 | |
| .NET MAUI Blazor Hybrid | UI-хост |
| Blazor (Razor Components) | UI |
| Entity Framework Core + SQLite | Локальное хранилище |
| MailKit | IMAP/SMTP |
| Portable.BouncyCastle | PGP |

---

## Быстрый старт

```bash
# Установить MAUI workload
dotnet workload install maui

# Клонировать
git clone https://github.com/your-username/echat.git
cd echat/src/EChat.Maui

# Windows
dotnet run --framework net10.0-windows10.0.19041.0

# Android (с подключённым устройством или эмулятором)
dotnet run --framework net10.0-android
```

**Требования:** .NET 10 SDK, Visual Studio 2022/2026 с workload MAUI.

---

## Поддерживаемые платформы

| Платформа | Статус |
|---|---|
| Windows 10/11 (x64) | Поддерживается |
| Android 7.0+ (API 24+) | Поддерживается |
| iOS | В планах |

---

## Текущее состояние (v0.1.0)

Что работает:
- Онбординг аккаунта с автодетектом IMAP/SMTP для популярных провайдеров
- Мультиаккаунт: добавление, переключение, удаление
- Горячая смена настроек без перезапуска (ReconnectAsync)
- 1-1 чаты и групповые чаты
- Батч-очередь исходящих сообщений
- Дедупликация входящих
- NTP-коррекция порядка сообщений
- Multi-device sync (DeviceSyncService)
- Адаптивный UI: Windows 3-колонки, Android одна панель

Что в разработке:
- Вложения (фото, файлы)
- PGP UI (ключи генерируются, интеграция с UI в процессе)
- Push-уведомления Android
- iOS

Не планируется:
- Typing indicators, онлайн-статусы, истории/сторис, стикеры, каналы, боты

---

## Лицензия

MIT
