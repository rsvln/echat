# εChat — Roadmap

> Живой документ. Обновляется после каждой сессии разработки.
> Для AI-агентов: читай этот файл в начале каждой сессии чтобы понять что уже сделано и что планируется.

---

## Сделано ✅

### Contact Management — Phase 1
- Contacts: composite PK `(AccountId, Email)` — изоляция контактов по аккаунту
- Поля Contact: `IsBlocked`, `BlockedAt`, `Notes`, `DisplayName`, `KeyFingerprint`, `Verified`
- ContactsModal: список контактов с поиском, блокировкой, заметками
- ChatInfoModal: редактирование имени контакта, заметок, блокировка из чата
- Миграция `ContactPerAccount`: raw SQL с `suppressTransaction: true` для обхода ограничений SQLite PRAGMA внутри транзакций

### GroupMember.DisplayName
- Поле `DisplayName` в `GroupMember` — хранит имя участника независимо от Contacts
- Протокол: `group-create` передаёт `member_names: {email: name}`, `group-member-add` передаёт `added_name`, `group-member-remove` передаёт `removed_name`
- `IncomingMessageService`: сохраняет DisplayName при получении, обновляет если пришло лучшее имя
- Системные сообщения ("X was added", "X left") используют `GroupMember.DisplayName` → `Contact.DisplayName` → полный email (не обрубленный)
- Миграция `GroupMemberDisplayName`

### Безопасность credentials
- `ICredentialProtector` + `IsProtected()` — интерфейс для шифрования паролей/ключей в БД
- `DpapiCredentialProtector` (Windows DPAPI) + `SecureStorageCredentialProtector` (Android AES-GCM/Keystore)
- Step 3d при старте: проверяет `IsProtected()` перед ре-шифрованием, пропускает если уже зашифровано

### AccountSetup
- Поле пароля для импорта зашифрованного бэкапа
- Кнопка Back (CSS `.page-back-btn`) при добавлении аккаунта из основного приложения

### NtpTimeService
- Замена `socket.ReceiveTimeout` (игнорировался async) на `CancellationTokenSource`
- Проверка `intPart == 0` и санитарная проверка (±10 лет от системных часов)
- HTTP HEAD fallback через mail-домены аккаунтов

### Логи
- Полный email в тегах `[yarustam@yandex.ru]` вместо обрубленного `[yarustam]`
- ImapService, IncomingMessageService

### Прочее
- Quiet Hours: работает, влияет на SyncProfile в указанный промежуток (почасовая гранулярность)

---

## Phase 2 — Per-contact ключевые пары (криптографический отзыв)

**Проблема**: сейчас у Alice один глобальный PGP-ключ. Если Bob получил её публичный ключ — он навсегда может шифровать ей сообщения. Удаление чата / блокировка не отзывает ключ у Bob'а.

**Решение**: вместо одного глобального ключа — per-contact ключевые пары.

```
Alice → Bob:   выдаёт K_bob  (публичный ключ, сгенерированный специально для этого отношения)
Alice → Carol: выдаёт K_carol (другой ключ)
Bob шифрует Alice только ключом K_bob.
Alice держит приватный ключ k_bob в таблице ContactInboundKey.
```

**Отзыв**:
- `ContactInboundKey.RevokedAt = now` — перестаём расшифровывать сообщения, зашифрованные на K_bob
- Новый K_bob_v2 генерируется, но Bob его не получает → он не может написать ничего что мы откроем

### Что нужно сделать

1. **Новая таблица `ContactInboundKey`**
   ```
   ContactEmail  string PK (partial)
   AccountId     string PK (partial)
   PublicKey     string   — отдаём контакту
   PrivateKey    string   — храним у себя (зашифрован DPAPI/Keystore)
   CreatedAt     DateTimeOffset
   RevokedAt     DateTimeOffset?  — null = активен, !null = отозван
   ```
   EF миграция.

2. **Изменить Autocrypt-рекламу**
   Вместо глобального ключа — в заголовке `Autocrypt` рекламируем per-contact ключ.
   После верификации через invite → переходим на K_contact, глобальный для этого адресата больше не рекламируется.

3. **Изменить логику расшифровки** (`IncomingMessageService` / `EmailTransportService`)
   - Пробовать per-contact приватный ключ (`ContactInboundKey` where `RevokedAt == null`)
   - Fallback на глобальный ключ аккаунта (для писем до перехода)
   - Если ключ найден но `RevokedAt != null` → сообщение выбрасываем (отзыв работает)

4. **Invite flow**
   При верификации контакта через invite — генерируем `ContactInboundKey`, отправляем публичный ключ контакту.

5. **Отзыв в UI**
   Кнопка "Revoke / Block" в ContactsModal → `RevokedAt = now`, чат архивируется.

6. **Синхронизация между устройствами**
   `ContactInboundKey` должна синхронизироваться через IMAP (новый sync-тип).

### Глобальный ключ остаётся для
- Invite flow (HMAC, первый контакт — до верификации)
- Группы (там своя ключевая пара на группу)

### Статус
- [ ] Не начата

---

## Известные ограничения / технический долг

- `LocalPublicKey` / `LocalPrivateKey` в модели `Account` — зарезервировано для Phase 2, нигде не заполняется
- `PlaintextCredentialProtector` — заглушка для платформ без нативного хранилища
- IMAP как журнал событий: если событие (удаление группы) не было записано в IMAP — ресинк не сможет его воспроизвести. Zombie-группы из тестовых сессий не вычищаются при ресинке
- Дублирующиеся группы при восстановлении БД: tombstone теряется, старые group-create письма пересоздают группы
