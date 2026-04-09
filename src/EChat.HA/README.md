# eChat Notify для Home Assistant

Интеграция Home Assistant для отправки уведомлений в echat через email-based протокол с поддержкой PGP шифрования (Autocrypt).

## Установка через HACS

1. Установите [HACS](https://hacs.xyz/) если ещё не установлен
2. Перейдите в HACS > Integrations
3. Нажмите ⋮ > Custom repositories
4. Добавьте репозиторий: `https://github.com/yourrepo/echat`
5. Выберите категорию: Integration
6. Найдите "eChat Notify" и нажмите Install

## Установка вручную

Скопируйте папку `custom_components/echat_notify/` в директорию `custom_components` вашего Home Assistant:

```
custom_components/
└── echat_notify/
    ├── __init__.py
    ├── config_flow.py
    ├── notify.py
    ├── client.py
    ├── crypto.py
    ├── keystore.py
    ├── const.py
    ├── strings.json
    └── manifest.json
```

После этого перезапустите Home Assistant.

## Настройка

1. Перейдите в **Настройки > Устройства и службы > Добавить интеграцию**
2. Найдите "eChat Notify"
3. Введите данные бота:
   - **Bot Email** — email адрес бота (например, `ha-bot@gmail.com`)
   - **Bot Password** — пароль или App Password
   - **SMTP Server** — оставьте пустым для автоопределения
   - **SMTP Port** — оставьте пустым для автоопределения
   - **Use SSL** — рекомендуется включено (port 465)

### Настройка SMTP для популярных провайдеров

| Провайдер | SMTP сервер | Порт | SSL |
|----------|-------------|------|-----|
| Gmail | smtp.gmail.com | 465 | Да |
| Yandex | smtp.yandex.ru | 465 | Да |
| Mail.ru | smtp.mail.ru | 465 | Да |
| Outlook | smtp-mail.outlook.com | 587 | Нет |

### Gmail: как получить App Password

1. Включите двухфакторную аутентификацию: https://myaccount.google.com/security
2. Создайте App Password: https://myaccount.google.com/apppasswords
3. Используйте App Password вместо обычного пароля

## Использование

### Отправка уведомления

```yaml
automation:
  - alias: "Уведомление об открытии двери"
    trigger:
      platform: state
      entity_id: binary_sensor.front_door
      to: "on"
    action:
      - service: notify.echat
        data:
          message: "Внимание! Дверь открыта"
          target: "user@example.com"
```

### Несколько получателей

```yaml
action:
  - service: notify.echat
    data:
      message: "Тревога!"
      target:
        - "user1@example.com"
        - "user2@example.com"
```

### Использование в скриптах

```yaml
script:
  send_weather_report:
    sequence:
      - action: notify.echat
        data:
          message: >
            Погода сегодня: {{ states('sensor.weather_temperature') }}°C
            Влажность: {{ states('sensor.weather_humidity') }}%
          target: "family@example.com"
```

## Как это работает

### Автоматическое шифрование

1. **Первый контакт** — сообщение отправляется открытым текстом с Autocrypt заголовком (реклама ключа)
2. **Повторные контакты** — сообщения шифруются автоматически

### Autocrypt Key Exchange

При отправке первого сообщения контакту:
- Бот отправляет свой публичный PGP ключ в заголовке `Autocrypt`
- echat клиент получателя сохраняет этот ключ
- Все последующие сообщения шифруются

### Хранение ключей

- Публичные ключи контактов сохраняются в локальной SQLite базе
- При необходимости можно вручную добавить ключ через Keystore API

## Команды

### Проверка статуса интеграции

```bash
# В терминале Home Assistant
ha core logs | grep echat
```

### Просмотр сохранённых контактов

Ключи контактов хранятся в файле `echat_keystore.db` в директории интеграции.

## Требования

- Home Assistant 2024.1+
- Python 3.10+
- GPGME библиотека (для python-gnupg)
- Пакет `python-gnupg`

## Структура проекта

```
EChat.HA/
└── custom_components/
    └── echat_notify/
        ├── __init__.py      # Точка входа интеграции
        ├── config_flow.py   # Мастер настройки UI
        ├── notify.py        # Notify platform
        ├── client.py        # SMTP клиент + Chat headers
        ├── crypto.py        # PGP операции
        ├── keystore.py      # Хранение ключей
        ├── const.py         # Константы
        ├── strings.json     # Локализация
        └── manifest.json    # Манифест компонента
```

## Troubleshooting

### Ошибка "Authentication failed"

- Для Gmail: убедитесь что используете App Password, не обычный пароль
- Проверьте что SMTP включён в настройках почтового сервиса

### Сообщения не шифруются

- Это нормально для первого контакта — ключ ещё не сохранён
- Убедитесь что получатель добавил бота в контакты и принял ключ

### Ошибка "gpg: can't connect to the agent"

```bash
# Установите gpg-agent
sudo apt install gnupg-agent
```

## Лицензия

MIT
