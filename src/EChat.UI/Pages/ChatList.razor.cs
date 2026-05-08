using EChat.Core.Crypto;
using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Protocol;
using EChat.Core.Services;
using EChat.Core.Sync;
using EChat.Core.Transport;
using EChat.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Net.Codecrete.QrCodeGenerator;
using static EChat.Core.ServiceCollectionExtensions;

namespace EChat.UI.Pages;

public partial class ChatList
{
    // Accounts
    private List<Account>? accounts;
    private string? activeAccountId;
    private Account? activeAccount;
    private const string SubjectLine = "[eChat]";

    // Mobile account menu
    private bool showMobileAccountMenu = false;

    // Shared
    private List<Chat>? chats;
    private Dictionary<string, (string Sender, string? Content, DateTimeOffset? Timestamp, bool HasImage, bool HasFile)> chatLastMessages = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> contactDisplayNames = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> memberColors = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> encryptedContactEmails = new(StringComparer.OrdinalIgnoreCase);
    private string searchQuery = string.Empty;

    private IEnumerable<Chat> FilteredChats =>
        chats?.Where(c => string.IsNullOrEmpty(searchQuery) ||
                          c.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
              .OrderByDescending(c => chatLastMessages.TryGetValue(c.ChatId, out var lm)
                  ? lm.Timestamp ?? c.LastActivityAt
                  : c.LastActivityAt) ?? Enumerable.Empty<Chat>();

    // Desktop only
    private string? selectedChatId;

    // Chat list context menu
    private bool _showChatCtx;
    private bool _chatCtxConfirmDelete;
    private double _chatCtxX, _chatCtxY;
    private Chat? _chatCtxChat;
    private Chat? selectedChat;
    private List<ChatMessage>? messages;
    private List<string>? recipients;
    private string messageText = string.Empty;
    // Suppresses the automatic post-event StateHasChanged Blazor calls after every async handler.
    // Set to true during text input to prevent Blazor from writing value="@messageText" back to
    // the textarea DOM node, which resets the cursor — particularly visible on Android WebView.
    private bool _suppressTextareaRender;
    // Tracks message IDs for which we already sent a read receipt this session,
    // so we never send duplicate receipts and avoid ping-pong email loops.
    private readonly HashSet<string> _sentReceiptIds = new(StringComparer.OrdinalIgnoreCase);
    private ElementReference messagesContainer;
    private bool showChatInfo = false;
    private bool _scrollToBottom = false;
    private string? _openedChatId = null;  // chatId whose scroll was last restored
    private double _pendingScrollPos = -1; // position to restore on next render (-1 = none)

    // SMTP rate-limit state — per-account, multiple accounts can be rate-limited simultaneously
    // Key = accountId, Value = earliest retry time
    private readonly Dictionary<string, DateTimeOffset> _rateLimitedAccounts = new(StringComparer.OrdinalIgnoreCase);
    private bool _smtpRateLimited => _rateLimitedAccounts.ContainsKey(activeAccountId ?? "");
    private DateTimeOffset? _smtpRateLimitedUntil => _rateLimitedAccounts.GetValueOrDefault(activeAccountId ?? "");

    // Archived chats toggle
    private bool showArchived = false;

    // Contact display names: email → name
    private Dictionary<string, string> contactNames = new(StringComparer.OrdinalIgnoreCase);

    // Unread counts per account (sum of non-muted, non-archived chats)
    private Dictionary<string, int> accountUnreadCounts = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, List<Attachment>> _messageAttachments = new();

    // Pending attachments
    private List<PendingAttachment> pendingAttachments = new();

    // MessageId → reactions
    private Dictionary<string, List<MessageReaction>> _messageReactions = new(StringComparer.OrdinalIgnoreCase);

    // MessageId → ChatMessage for quick quoted-message lookup
    private Dictionary<string, ChatMessage> messagesById = new(StringComparer.OrdinalIgnoreCase);

    // Reply / edit / forward state
    private ChatMessage? replyingToMessage;
    private ChatMessage? editingMessage;
    private ChatMessage? forwardingMessage;
    private bool showForwardDialog;

    // Send on Enter mode (true = Enter sends, false = Ctrl+Enter sends)
    private bool sendOnEnter = true;

    // Format context menu
    private bool showFormatMenu = false;
    private double formatMenuX;
    private double formatMenuY;
    private string selectedText = "";

    // Emoji & attach
    private bool showEmojiPicker = false;
    private bool showAttachMenu = false;
    private readonly string[] emojiList = new[] {
        "😀","😃","😄","😁","😅","😂","🤣","😊","😇","🥰","😍","🤩","😘","😗","😚","😙",
        "🥲","😋","😛","😜","🤪","😝","🤑","🤗","🤭","🤫","🤔","🫡","🤐","🤨","😐","😑",
        "😶","🫥","😏","😒","🙄","😬","🤥","😌","😔","😪","🤤","😴","😷","🤒","🤕","🤢",
        "🤮","🥵","🥶","🥴","😵","🤯","🤠","🥳","🥸","😎","🤓","🧐","😕","🫤","😟","🙁",
        "😮","😯","😲","😳","🥺","🥹","😦","😧","😨","😰","😥","😢","😭","😱","😖","😣",
        "😞","😓","😩","😫","🥱","😤","😡","😠","🤬","😈","👿","💀","☠️","💩","🤡","👹",
        "👺","👻","👽","👾","🤖","😺","😸","😹","😻","😼","😽","🙀","😿","😾","🙈","🙉",
        "🙊","💋","💌","💘","💝","💖","💗","💓","💞","💕","💟","❣️","💔","❤️‍🔥","❤️‍🩹",
        "❤️","🧡","💛","💚","💙","💜","🤎","🖤","🤍","💯","💢","💥","💫","💦","💨","🕳️",
        "👍","👎","👊","✊","🤛","🤜","👏","🙌","🫶","👐","🤲","🤝","🙏","✌️","🤞","🫰",
        "🤟","🤘","👌","🤌","🤏","👈","👉","👆","👇","☝️","✋","🤚","🖐️","🖖","🫱","🫲",
        "🫳","🫴","👋","🤙","💪","🦾","🖕","✍️","🎉","🎊","🎈","🎁","🎀","🎗️","🏆","🥇",
        "🥈","🥉","⚽","🏀","🏈","⚾","🥎","🎾","🏐","🏉","🥏","🎱","🪀","🏓","🏸","🏒",
        "🔥","⭐","🌟","✨","💫","🌈","☀️","🌤️","⛅","🌥️","☁️","🌦️","🌧️","⛈️","🌩️","🌪️",
        "🌫️","🌬️","🌀","💧","💦","☔","☂️","🌊","🌴","🌵","🌾","🌿","☘️","🍀","🍁","🍂","🍃"
    };

    // New chat modal visibility
    private bool showNewChat = false;
    private bool showContacts = false;

    protected override async Task OnInitializedAsync()
    {
        _instance = this;
        // Event subscriptions must happen before any await so they're not missed.
        ChatEvents.ChatUpdated += OnChatUpdated;
        TransportService.RateLimitStarted += OnRateLimitStarted;
        TransportService.RateLimitCleared += OnRateLimitCleared;
        try
        {
            await LoadAccountsAsync();
            await LoadChatsAsync();
            using var scope = ScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var contacts = await db.Contacts.AsNoTracking()
                .Where(c => c.AccountId == activeAccountId)
                .OrderBy(c => c.DisplayName).ToListAsync();
            contactNames = contacts.ToDictionary(c => c.Email, c => c.DisplayName ?? c.Email.Split('@')[0], StringComparer.OrdinalIgnoreCase);
            // Reflect current state in case rate-limit was already active before this page loaded
            if (TransportService.IsRateLimited && activeAccountId != null)
                _rateLimitedAccounts[activeAccountId] = TransportService.RateLimitedUntil ?? DateTimeOffset.UtcNow.AddMinutes(5);
            sendOnEnter = bool.Parse(Prefs.Get("send_on_enter", "True"));

            // Apply saved log level to FileLogger on every startup
            var savedLogLevel = Prefs.Get("log_level", EChat.Core.Services.AppLogLevel.Info.ToString());
            if (Enum.TryParse<EChat.Core.Services.AppLogLevel>(savedLogLevel, out var lvl))
                FileLogger.MinLevel = lvl;
        }
        catch (Exception ex)
        {
            FileLogger.Write("ERROR", "ChatList.OnInitialized", $"Failed to initialize: {ex.Message}");
            chats ??= new List<Chat>();
            contactNames ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private bool _firstRender = true;
    private bool _isReturning = false;

    protected override async Task OnParametersSetAsync()
    {
        if (!_firstRender)
        {
            try
            {
                await LoadChatsAsync();
            }
            catch (Exception ex)
            {
                FileLogger.Write("ERROR", "ChatList.OnParametersSet", $"Failed to reload chats: {ex.Message}");
            }
        }
        // First render: JS not available yet during prerendering.
        // Saved chat restoration is handled in OnAfterRenderAsync(firstRender: true).
        _firstRender = false;
    }

    public void Dispose()
    {
        ChatEvents.ChatUpdated -= OnChatUpdated;
        TransportService.RateLimitStarted -= OnRateLimitStarted;
        TransportService.RateLimitCleared -= OnRateLimitCleared;
    }

    private bool IsAccountRateLimited(string accountId) => _rateLimitedAccounts.ContainsKey(accountId);
    private string? AccountRateLimitTime(string accountId) =>
        _rateLimitedAccounts.TryGetValue(accountId, out var t) ? t.ToLocalTime().ToString("HH:mm") : null;

    private void OnRateLimitStarted(DateTimeOffset retryAfter)
    {
        if (activeAccountId != null)
            _rateLimitedAccounts[activeAccountId] = retryAfter;
        InvokeAsync(StateHasChanged);
    }

    private void OnRateLimitCleared()
    {
        if (activeAccountId != null)
            _rateLimitedAccounts.Remove(activeAccountId);
        InvokeAsync(StateHasChanged);
    }

    protected override bool ShouldRender()
    {
        if (_suppressTextareaRender)
        {
            _suppressTextareaRender = false; // auto-reset: next render (for any reason) goes through
            return false;
        }
        return true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JS.InvokeVoidAsync("setupTextareaResize");

            // Restore previously selected chat (JS only available after first render)
            var savedChatId = await JS.InvokeAsync<string?>("localStorage.getItem", "echat_selected_chat");
            if (!string.IsNullOrEmpty(savedChatId) && savedChatId != selectedChatId)
            {
                _isReturning = true;
                selectedChatId = savedChatId;
                selectedChat = chats?.FirstOrDefault(c => c.ChatId == savedChatId);
                if (selectedChat != null)
                {
                    await LoadMessagesAsync();
                    await LoadRecipientsAsync();
                    await LoadReactionsAsync();
                    await ClearUnreadAsync(savedChatId);
                    _ = SendReadReceiptsAsync(savedChatId);
                    _openedChatId = savedChatId;
                    var (savedPos, wasAtBottom) = await LoadScrollPosAsync(savedChatId);
                    _pendingScrollPos = savedPos;
                    _scrollToBottom = wasAtBottom;
                    StateHasChanged();
                }
            }
        }
        await Task.Yield();

        if (_scrollToBottom)
        {
            FileLogger.Write("DEBUG", "Scroll", $"BRANCH scrollToBottom, selectedChatId={selectedChatId}");
            _scrollToBottom = false;
            _openedChatId = selectedChatId;
            _pendingScrollPos = -1;
            try { await JS.InvokeVoidAsync("scrollChatToBottom"); } catch (Exception ex) { FileLogger.Write("ERROR", "Scroll", $"scrollChatToBottom failed: {ex.Message}"); }
        }
        else if (_isReturning && _pendingScrollPos >= 0 && selectedChatId != null)
        {

            _isReturning = false;
            var pos = _pendingScrollPos;
            _pendingScrollPos = -1;
            try { await JS.InvokeVoidAsync("setChatScrollTop", pos); } catch { }
        }
        else if (_pendingScrollPos >= 0 && _openedChatId == selectedChatId)
        {

            var pos = _pendingScrollPos;
            _pendingScrollPos = -1;
            try { await JS.InvokeVoidAsync("setChatScrollTop", pos); } catch { }
        }
        else
        {

        }
    }

    private void OnChatUpdated(string chatId)
    {
        InvokeAsync(async () =>
        {
            try
            {
                await LoadChatsAsync();
                if (selectedChatId == chatId)
                {
                    // Check if the currently selected chat was deleted
                    var chatStillExists = chats?.Any(c => c.ChatId == chatId) == true;
                    if (!chatStillExists)
                    {
                        selectedChatId = null;
                        selectedChat = null;
                        messages = null;
                        recipients = null;
                        try { await JS.InvokeVoidAsync("localStorage.removeItem", "echat_selected_chat"); } catch { }
                        StateHasChanged();
                        return;
                    }
                    await LoadMessagesAsync();
                    await ClearUnreadAsync(chatId);
                    _ = SendReadReceiptsAsync(chatId);
                    _scrollToBottom = true;
                }
                StateHasChanged();
            }
            catch (Exception ex)
            {
                FileLogger.Write("ERROR", "ChatList.OnChatUpdated", $"Failed to update chat {chatId}: {ex.Message}");
            }
        });
    }

    private async Task LoadAccountsAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        accounts = await db.Accounts.AsNoTracking().ToListAsync();
        activeAccount = accounts.FirstOrDefault(a => a.IsActive)
                        ?? accounts.FirstOrDefault();
        activeAccountId = activeAccount?.AccountId;

        if (activeAccount != null && string.IsNullOrEmpty(UserContext.AccountId))
        {
            var deviceId = Prefs.Get("device_id", string.Empty);
            UserContext.Initialize(activeAccount.AccountId, activeAccount.Email, deviceId);
        }

    }

    private async Task LoadChatsAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var query = db.Chats.AsNoTracking().AsQueryable();
        query = query.Where(c => !c.Deleted);
        if (!showArchived)
            query = query.Where(c => !c.Archived);
        if (!string.IsNullOrEmpty(activeAccountId))
        {
            // Strict per-account filter: each account owns its own chat rows.
            // Do NOT pull in other accounts' group chat rows here — the cross-account group
            // membership query used to be here, but it caused the dedup (line below) to keep
            // account A's group chat row (most-recently-updated, UnreadCount=0 for sender)
            // instead of account B's own row (UnreadCount=1 for recipient).
            // IncomingMessageService now creates a dedicated row per account for every group,
            // so each account's group chats are fully represented by their own rows.
            query = query.Where(c => c.AccountId == activeAccountId);
        }

        var list = await query.ToListAsync();

        // Deduplicate group chats by GroupId: keep only the most recently active chat per group.
        // This handles both: (1) same-account duplicates from repeated group-create IMAP syncs
        // when GroupId was null, and (2) cross-account duplicates where multiple accounts have
        // their own Chat row for the same group.
        var groupChatIdsToKeep = list
            .Where(c => c.Type == ChatType.Group && !string.IsNullOrEmpty(c.GroupId))
            .GroupBy(c => c.GroupId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.LastActivityAt ?? c.CreatedAt).First().ChatId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Keep 1:1 chats, group chats with no GroupId (can't dedup), and the winning group chat per GroupId
        list = list.Where(c =>
            c.Type != ChatType.Group ||
            string.IsNullOrEmpty(c.GroupId) ||
            groupChatIdsToKeep.Contains(c.ChatId)).ToList();

        // Sort will be re-applied after last-message timestamps are loaded below.
        chats = list;

        // Load contact display names for sender labels in preview
        var contacts = await db.Contacts.AsNoTracking()
            .Where(c => c.AccountId == activeAccountId)
            .ToListAsync();
        contactDisplayNames = contacts
            .Where(c => !string.IsNullOrEmpty(c.DisplayName))
            .ToDictionary(c => c.Email, c => c.DisplayName!, StringComparer.OrdinalIgnoreCase);

        // Build set of encrypted chat IDs: for 1:1 chats, check if the linked Contact has a public key.
        var contactsWithKeys = contacts
            .Where(c => c.PublicKey != null)
            .Select(c => c.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        encryptedContactEmails = chats
            .Where(c => c.Type == ChatType.OneToOne && c.ContactEmail != null && contactsWithKeys.Contains(c.ContactEmail))
            .Select(c => c.ChatId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Load last message per chat — N+1 per chat, each a LIMIT 1 query.
        // Load Timestamp into memory to avoid any DateTimeOffset SQL translation issues,
        // then sort in C#. Wrapped in try-catch so preview failure never blanks the screen.
        try
        {
            var newLastMessages = new Dictionary<string, (string Sender, string? Content, DateTimeOffset? Timestamp, bool HasImage, bool HasFile)>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in chats)
            {
                var rows = await db.Messages
                    .AsNoTracking()
                    .Where(m => m.ChatId == c.ChatId)
                    .Select(m => new { Sender = m.Sender ?? "", m.Content, m.Timestamp, m.MessageId })
                    .ToListAsync();
                var last = rows.OrderByDescending(m => m.Timestamp).FirstOrDefault();
                if (last != null)
                {
                    var atts = await db.Attachments
                        .AsNoTracking()
                        .Where(a => a.MessageId == last.MessageId)
                        .Select(a => a.ContentType)
                        .ToListAsync();
                    newLastMessages[c.ChatId] = (last.Sender, last.Content, last.Timestamp,
                        atts.Any(x => x.StartsWith("image/", StringComparison.OrdinalIgnoreCase)),
                        atts.Any(x => !x.StartsWith("image/", StringComparison.OrdinalIgnoreCase)));
                }
            }
            chatLastMessages = newLastMessages;

            // Sort by last message timestamp; fall back to LastActivityAt for chats with no messages.
            chats = chats
                .OrderByDescending(c => chatLastMessages.TryGetValue(c.ChatId, out var lm)
                    ? lm.Timestamp ?? c.LastActivityAt
                    : c.LastActivityAt)
                .ToList();
        }
        catch (Exception ex)
        {
            FileLogger.Write("WARN", "ChatList", $"Failed to load last messages: {ex.Message}");
            chatLastMessages = new Dictionary<string, (string Sender, string? Content, DateTimeOffset? Timestamp, bool HasImage, bool HasFile)>(StringComparer.OrdinalIgnoreCase);
        }

        await UpdateAccountUnreadCountsAsync();
    }

    private string GetLastMessagePreview(Chat chat)
    {
        if (!chatLastMessages.TryGetValue(chat.ChatId, out var info))
            return string.Empty;

        var text = StripFormatting(info.Content ?? string.Empty)
            .Replace("\r", "").Replace("\n", " ").Trim();
        if (text.Length > 55) text = text[..52] + "…";

        // If no text, show attachment placeholder
        if (string.IsNullOrEmpty(text))
        {
            if (info.HasImage && info.HasFile)       text = "[Image] [File]";
            else if (info.HasImage)                  text = "[Image]";
            else if (info.HasFile)                   text = "[File]";
        }

        var isMe = string.Equals(info.Sender, activeAccount?.Email, StringComparison.OrdinalIgnoreCase);
        var senderLabel = isMe
            ? "You"
            : (contactDisplayNames.TryGetValue(info.Sender ?? "", out var name)
                ? name
                : (info.Sender ?? "").Split('@')[0]);

        return $"{senderLabel}: {text}";
    }

    private static string StripFormatting(string text) => HtmlFormatter.StripFormatting(text);

    private async Task UpdateAccountUnreadCountsAsync()
    {
        if (accounts == null) return;
        using var scope = ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var unreadChats = await db.Chats
            .AsNoTracking()
            .Where(c => !c.Muted && !c.Archived && !c.Deleted && c.UnreadCount > 0)
            .ToListAsync();
        // Each account's unread count comes purely from its own chat rows.
        // Cross-account group membership attribution was removed: IncomingMessageService
        // now creates a dedicated Chat row per account for every group, so each account's
        // UnreadCount is tracked independently and correctly attributed to that account.
        accountUnreadCounts = accounts.ToDictionary(
            a => a.AccountId,
            a => unreadChats.Where(c => c.AccountId == a.AccountId).Sum(c => c.UnreadCount),
            StringComparer.OrdinalIgnoreCase);

        // Sync taskbar/app-icon badge with the real DB state every time unread counts
        // are recomputed. This fixes phantom badges that appear when the in-memory count
        // diverges from the database (e.g. on startup or after a sync from another device).
        PlatformService.UpdateBadge(accountUnreadCounts.Values.Sum());
    }

    private async Task OnChatArchived(string chatId)
    {
        await LoadChatsAsync();
        // If the archived chat was selected, deselect it (unless showing archived)
        if (!showArchived && selectedChatId == chatId)
        {
            selectedChatId = null;
            selectedChat = null;
            messages = null;
            recipients = null;
        }
        StateHasChanged();
    }

    private async Task OnChatUnarchived()
    {
        await LoadChatsAsync();
        StateHasChanged();
    }

    private async Task DeleteChatRemote(string chatId)
    {
        var chat = await DbContext.Chats.FindAsync(chatId);
        if (chat == null) return;

        // Delete the corresponding emails from the IMAP server before local cleanup
        _ = Task.Run(() => TransportService.DeleteChatImapMessagesAsync(chatId));

        if (chat.Type == ChatType.Group)
        {
            var myEmail = UserContext.UserEmail;
            var effectiveGroupId = chat.GroupId ?? chatId;
            var members = await DbContext.GroupMembers
                .Where(m => m.GroupId == effectiveGroupId)
                .Select(m => m.MemberEmail)
                .ToListAsync();

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "group-delete",
                group_id = effectiveGroupId,
                deleted_by = myEmail
            });

            // Include the admin's own email so other devices learn about the deletion
            // via the CC copy. Without this, the admin's other devices never see the
            // group-delete and the chat stays visible there indefinitely.
            var allRecipients = members.ToList();
            if (!allRecipients.Contains(myEmail, StringComparer.OrdinalIgnoreCase))
                allRecipients.Add(myEmail);

            foreach (var email in allRecipients)
            {
                var contact = await DbContext.Contacts.FindAsync(activeAccountId, email);
                var isSelf = email.Equals(myEmail, StringComparison.OrdinalIgnoreCase);
                try
                {
                    await TransportService.SendMessageAsync(new OutgoingMessage
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Content = payload,
                        Recipients = new List<string> { email },
                        RecipientPublicKey = isSelf ? null : contact?.PublicKey,
                        Timestamp = NtpClock.UtcNow,
                        Type = MessageType.System,
                        SystemType = "group-delete",
                        GroupId = effectiveGroupId,
                        Tier = BatchTier.Immediate,
                        Encrypt = !isSelf && !string.IsNullOrEmpty(contact?.PublicKey)
                    });
                }
                catch { /* non-fatal */ }
            }

            // Mark deleted and clean up locally
            await DbContext.GroupMembers
                .Where(m => m.GroupId == effectiveGroupId)
                .ExecuteDeleteAsync();
        }

        else
        {
            var recipients = await GetRecipientsForChatAsync(chat);
            // Include own email so other devices learn about the deletion
            if (!recipients.Contains(UserContext.UserEmail))
                recipients.Add(UserContext.UserEmail);

            if (recipients.Any())
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "chat-delete",
                    chat_id = chatId,
                    deleted_by = UserContext.UserEmail
                });

                foreach (var email in recipients)
                {
                    var contact = await DbContext.Contacts.FindAsync(activeAccountId, email);
                    var isSelf = email.Equals(UserContext.UserEmail, StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        await TransportService.SendMessageAsync(new OutgoingMessage
                        {
                            MessageId = Guid.NewGuid().ToString(),
                            Content = payload,
                            Recipients = new List<string> { email },
                            RecipientPublicKey = isSelf ? null : contact?.PublicKey,
                            Timestamp = NtpClock.UtcNow,
                            Type = MessageType.System,
                            SystemType = "chat-delete",
                            Tier = BatchTier.Immediate,
                            Encrypt = !isSelf && !string.IsNullOrEmpty(contact?.PublicKey)
                        });
                    }
                    catch { }
                }
            }
        }

        chat.Deleted = true;
        // Permanent delete marker — prevents resurrection from stale group-create IMAP messages
        if (chat.Type == ChatType.Group)
            chat.TombstoneVersion = int.MaxValue;
        await DbContext.SaveChangesAsync();

        await LoadChatsAsync();
        if (selectedChatId == chatId)
        {
            selectedChatId = null;
            selectedChat = null;
            messages = null;
            recipients = null;
        }
        StateHasChanged();
    }

    private async Task LeaveGroupRemote(string chatId)
    {
        var chat = await DbContext.Chats.FindAsync(chatId);
        if (chat == null) return;

        var groupId = chat.GroupId ?? chatId;
        var myEmail = UserContext.UserEmail;
        var members = await DbContext.GroupMembers
            .Where(m => m.GroupId == groupId && m.MemberEmail != myEmail)
            .Select(m => m.MemberEmail)
            .ToListAsync();

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "group-leave",
            group_id = groupId,
            leaving_email = myEmail
        });

        foreach (var email in members)
        {
            var contact = await DbContext.Contacts.FindAsync(activeAccountId, email);
            try
            {
                await TransportService.SendMessageAsync(new OutgoingMessage
                {
                    MessageId = Guid.NewGuid().ToString(),
                    Content = payload,
                    Recipients = new List<string> { email },
                    RecipientPublicKey = contact?.PublicKey,
                    Timestamp = NtpClock.UtcNow,
                    Type = MessageType.System,
                    SystemType = "group-leave",
                    GroupId = groupId,
                    Tier = BatchTier.Immediate,
                    Encrypt = !string.IsNullOrEmpty(contact?.PublicKey)
                });
            }
            catch { }
        }

        // Remove self from group members and mark chat as deleted locally
        var selfMember = await DbContext.GroupMembers.FindAsync(groupId, myEmail);
        if (selfMember != null) DbContext.GroupMembers.Remove(selfMember);
        chat.Deleted = true;
        await DbContext.SaveChangesAsync();

        await LoadChatsAsync();
        if (selectedChatId == chatId)
        {
            selectedChatId = null;
            selectedChat = null;
            messages = null;
            recipients = null;
        }
        StateHasChanged();
    }

    private void OpenChatCtxMenu(MouseEventArgs e, Chat chat)
    {
        _chatCtxChat = chat;
        _chatCtxX = e.ClientX;
        _chatCtxY = e.ClientY;
        _chatCtxConfirmDelete = false;
        _showChatCtx = true;
    }

    private void CloseChatCtx()
    {
        _showChatCtx = false;
        _chatCtxConfirmDelete = false;
        _chatCtxChat = null;
    }

    private async Task ToggleMutedCtx()
    {
        if (_chatCtxChat == null) return;
        var newMuted = !_chatCtxChat.Muted;
        await DbContext.Chats
            .Where(c => c.ChatId == _chatCtxChat.ChatId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Muted, newMuted));
        var local = chats?.FirstOrDefault(c => c.ChatId == _chatCtxChat.ChatId);
        if (local != null) local.Muted = newMuted;
        _chatCtxChat.Muted = newMuted;
        CloseChatCtx();
    }

    private async Task ToggleArchiveCtx()
    {
        if (_chatCtxChat == null) return;
        var chatId = _chatCtxChat.ChatId;
        var archive = !_chatCtxChat.Archived;
        CloseChatCtx();
        await ToggleArchiveFromMenu(chatId, archive);
    }

    private async Task CtxDeleteGroupAsAdmin()
    {
        if (_chatCtxChat == null) return;
        var chatId = _chatCtxChat.ChatId;
        CloseChatCtx();
        await DeleteChatRemote(chatId);
    }

    private async Task CtxLeaveGroup()
    {
        if (_chatCtxChat == null) return;
        var chatId = _chatCtxChat.ChatId;
        CloseChatCtx();
        await LeaveGroupRemote(chatId);
    }

    private async Task CtxDeleteOneToOne()
    {
        if (_chatCtxChat == null) return;
        var chatId = _chatCtxChat.ChatId;
        CloseChatCtx();
        await DeleteChatRemote(chatId);
    }

    private async Task ToggleArchiveFromMenu(string chatId, bool archive)
    {
        var chat = await DbContext.Chats.FindAsync(chatId);
        if (chat == null) return;
        chat.Archived = archive;
        await DbContext.SaveChangesAsync();
        if (archive)
            await OnChatArchived(chatId);
        else
            await OnChatUnarchived();
    }

    private async Task SwitchAccount(string accountId)
    {
        if (accountId == activeAccountId) return;

        var oldAccountId = activeAccountId!;

        using var scope = ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var dbAccounts = await db.Accounts.ToListAsync();
        foreach (var acc in dbAccounts)
            acc.IsActive = acc.AccountId == accountId;
        await db.SaveChangesAsync();

        foreach (var acc in accounts!)
            acc.IsActive = acc.AccountId == accountId;

        activeAccountId = accountId;
        activeAccount = accounts.First(a => a.AccountId == accountId);
        selectedChatId = null;
        selectedChat = null;
        messages = null;
        recipients = null;
        messageText = string.Empty;
        replyingToMessage = null;
        editingMessage = null;
        sendError = string.Empty;

        var deviceId = Prefs.Get("device_id", string.Empty);
        UserContext.Initialize(activeAccount.AccountId, activeAccount.Email, deviceId);
        Prefs.Set("user_email", activeAccount.Email);
        Prefs.Set("active_account_id", activeAccount.AccountId);

        // Notify MultiAccountImapManager to start worker for old account, stop worker for new
        ChatEvents.NotifyAccountSwitched(oldAccountId, accountId);

        // Load sync settings for the new account
        await SyncEngine.LoadSettingsAsync(accountId);

        _ = Task.Run(async () =>
        {
            try { await TransportService.ReconnectAsync(activeAccount, deviceId); }
            catch { /* non-fatal */ }
        });

        await LoadChatsAsync();
    }

    private void OpenAccountSettings()
    {
        if (activeAccountId != null)
            Navigation.NavigateTo($"/account-settings/{activeAccountId}");
        else
            Navigation.NavigateTo("/settings");
    }

    // ── New chat modal ──────────────────────────────────

    private void OpenNewChat() => showNewChat = true;
    private void CloseNewChat() => showNewChat = false;

    private async Task OnNewChatReady(string chatId)
    {
        await LoadChatsAsync();
        if (PlatformService.IsDesktop)
            await SelectChat(chatId);
        else
            Navigation.NavigateTo($"/chat/{chatId}");
    }


    // ── Desktop chat selection ──────────────────────────

    private async Task SelectChat(string chatId)
    {

        if (selectedChatId == chatId) return;

        if (selectedChatId != null)
        {
            try
            {
                var userWasAtBottom = await JS.InvokeAsync<bool>("isChatAtBottom");
                var pos = await JS.InvokeAsync<double>("getChatScrollTop");

                await SaveScrollPosAsync(selectedChatId, pos, userWasAtBottom);
            }
            catch { }
        }

        selectedChatId = chatId;
        selectedChat = chats?.FirstOrDefault(c => c.ChatId == chatId);
        messageText = string.Empty;

        memberColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (selectedChat != null && selectedChat.Type == ChatType.Group && !string.IsNullOrEmpty(selectedChat.GroupId))
        {
            using var scope = ScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            memberColors = await db.GroupMembers.AsNoTracking()
                .Where(m => m.GroupId == selectedChat.GroupId && m.NameColor != null)
                .ToDictionaryAsync(m => m.MemberEmail, m => m.NameColor!, StringComparer.OrdinalIgnoreCase);
        }

        await LoadMessagesAsync();
        await LoadRecipientsAsync();
        await LoadReactionsAsync();
        await ClearUnreadAsync(chatId);
        _ = SendReadReceiptsAsync(chatId);   // fire-and-forget: send read receipts in background
        await JS.InvokeVoidAsync("resetTextareaHeight");
        await JS.InvokeVoidAsync("localStorage.setItem", "echat_selected_chat", chatId);


        _openedChatId = chatId;
        var (savedPos, wasAtBottom) = await LoadScrollPosAsync(chatId);
        _pendingScrollPos = savedPos;
        _scrollToBottom = wasAtBottom;
        _isReturning = false;


        StateHasChanged();
    }

    private async Task SaveScrollPosAsync(string chatId, double pos, bool wasAtBottom)
    {
        try
        {

            var key = $"scroll_{chatId}";
            var value = $"{pos.ToString("F0")},{wasAtBottom}";
            var now = DateTimeOffset.UtcNow;

            var updated = await DbContext.Settings
                .Where(s => s.Key == key)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Value, value)
                    .SetProperty(x => x.UpdatedAt, now));

            if (updated == 0)
            {
                DbContext.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = now });
                await DbContext.SaveChangesAsync();

            }
            using var scope = ScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var verify = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);

        }
        catch (Exception ex)
        {

        }
    }

    private async Task<(double pos, bool wasAtBottom)> LoadScrollPosAsync(string chatId)
    {

        using var scope = ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var setting = await db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == $"scroll_{chatId}");
        double result = 0;
        bool wasAtBottom = false;
        if (setting != null)
        {
            var parts = setting.Value.Split(',');
            if (parts.Length >= 1 && double.TryParse(parts[0], out result))
            {
                wasAtBottom = parts.Length >= 2 && bool.TryParse(parts[1], out var b) && b;
            }
        }

        return (result, wasAtBottom);
    }

    private async Task SendReadReceiptsAsync(string chatId)
    {
        var myEmail = UserContext.UserEmail;
        if (string.IsNullOrEmpty(myEmail)) return;

        // Only messages from others that haven't been confirmed read yet.
        // Status == Read means the self-CC of our own receipt already arrived back,
        // so the receipt was delivered — no need to resend on every session restart.
        var allReceived = await DbContext.Messages
            .AsNoTracking()
            .Where(m => m.ChatId == chatId
                     && m.Sender != myEmail
                     && m.Status != MessageStatus.Read)
            .Select(m => new { m.MessageId, m.Sender })
            .ToListAsync();

        // Also skip IDs we already sent a receipt for in this session (prevents concurrent double-send)
        var receivedIds = allReceived
            .Where(m => !_sentReceiptIds.Contains(m.MessageId))
            .ToList();

        if (receivedIds.Count == 0) return;

        // Optimistically mark as in-flight to prevent concurrent duplicate sends.
        // We'll remove any IDs that actually fail so they can be retried next time.
        foreach (var m in receivedIds)
            _sentReceiptIds.Add(m.MessageId);

        var bySender = receivedIds.GroupBy(m => m.Sender);
        foreach (var grp in bySender)
        {
            var msgIds = grp.Select(m => m.MessageId).ToList();
            try
            {
                var contact = await DbContext.Contacts.FindAsync(activeAccountId, grp.Key);
                await TransportService.SendMessageAsync(new OutgoingMessage
                {
                    MessageId = Guid.NewGuid().ToString(),
                    Content = "read-notification",
                    Recipients = new List<string> { grp.Key },
                    RecipientPublicKey = contact?.PublicKey,
                    Timestamp = NtpClock.UtcNow,
                    Type = MessageType.ReadReceipt,
                    ReadOf = msgIds,
                    Tier = BatchTier.System,
                    Encrypt = !string.IsNullOrEmpty(contact?.PublicKey)
                });
            }
            catch (Exception ex)
            {
                // Remove failed IDs so they can be retried on the next call
                foreach (var id in msgIds)
                    _sentReceiptIds.Remove(id);
                FileLogger.Write("WARN", "SendReadReceipts", $"Failed to send read receipt to {grp.Key}: {ex.Message}");
            }
        }
    }

    private async Task ClearUnreadAsync(string chatId)
    {
        // ExecuteUpdateAsync issues a direct SQL UPDATE, bypassing the EF change tracker.
        // This is critical: FindAsync returns the tracker-cached entity (UnreadCount=0 from
        // a previous clear), causing an early return even when the DB has UnreadCount>0
        // (written by IncomingMessageService via a separate DbContext scope).
        var affected = await DbContext.Chats
            .Where(c => c.ChatId == chatId && c.UnreadCount > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UnreadCount, 0));
        if (affected == 0) return;
        var local = chats?.FirstOrDefault(c => c.ChatId == chatId);
        if (local != null) local.UnreadCount = 0;
        await UpdateAccountUnreadCountsAsync();

        // Update taskbar badge (Windows) / app-icon badge to reflect remaining unread.
        var totalUnread = accountUnreadCounts.Values.Sum();
        PlatformService.UpdateBadge(totalUnread);
    }

    private async Task LoadMessagesAsync()
    {
        if (selectedChatId == null) return;
        try
        {
            using var scope = ScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var list = await db.Messages
                .AsNoTracking()
                .Where(m => m.ChatId == selectedChatId)
                .ToListAsync();
            messages = list.OrderBy(m => m.DisplayTimestamp).ToList();
            messagesById = messages.ToDictionary(m => m.MessageId, StringComparer.OrdinalIgnoreCase);

            // Load attachments for all messages in this chat
            var atts = await db.Attachments
                .AsNoTracking()
                .Where(a => messages.Select(m => m.MessageId).Contains(a.MessageId))
                .ToListAsync();
            var uniqueAtts = atts
                .GroupBy(a => new { a.MessageId, a.FileName })
                .Select(g => g.First())
                .ToList();
            _messageAttachments = uniqueAtts.GroupBy(a => a.MessageId)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            messages ??= new List<ChatMessage>();
        }
    }

    private async Task LoadRecipientsAsync()
    {
        if (selectedChatId == null || selectedChat == null) return;

        using var scope = ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        if (selectedChat.Type == ChatType.Group)
        {
            // GroupId should equal ChatId for groups created/migrated correctly.
            // If GroupId is null (pre-migration row that missed the backfill), fall back to ChatId.
            var groupId = selectedChat.GroupId ?? selectedChat.ChatId;

            recipients = await db.GroupMembers
                .AsNoTracking()
                .Where(m => m.GroupId == groupId)
                .Select(m => m.MemberEmail)
                .ToListAsync();

            // If still empty, GroupMembers rows were lost (e.g. cleanup bug wiped them).
            // Recover from message senders across ALL chats sharing the same GroupId
            // (messages may live under a different ChatId due to multi-device duplicates).
            if (recipients.Count == 0)
            {
                var allChatIdsForGroup = await db.Chats
                    .AsNoTracking()
                    .Where(c => c.GroupId == groupId)
                    .Select(c => c.ChatId)
                    .ToListAsync();

                recipients = await db.Messages
                    .AsNoTracking()
                    .Where(m => allChatIdsForGroup.Contains(m.ChatId))
                    .Select(m => m.Sender)
                    .Distinct()
                    .ToListAsync();
            }
            return;
        }

        // 1. ContactEmail field — set when the chat was created or migrated (most reliable)
        if (!string.IsNullOrEmpty(selectedChat.ContactEmail))
        {
            recipients = new List<string> { selectedChat.ContactEmail };
            return;
        }

        // 2. Try to find from existing messages (reliable when ContactEmail is missing)
        var myEmail = UserContext.UserEmail;
        var otherSender = await db.Messages
            .AsNoTracking()
            .Where(m => m.ChatId == selectedChatId && m.Sender != myEmail)
            .Select(m => m.Sender)
            .FirstOrDefaultAsync();

        if (otherSender != null)
        {
            // Backfill ContactEmail so next time we skip the query
            await db.Chats
                .Where(c => c.ChatId == selectedChatId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ContactEmail, otherSender));
            selectedChat.ContactEmail = otherSender;
            recipients = new List<string> { otherSender };
            return;
        }

        // 3. Look up contact by DisplayName or Email matching the chat name
        var chatName = selectedChat.Name;
        var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c =>
            c.AccountId == activeAccountId && (c.DisplayName == chatName || c.Email == chatName));

        if (contact != null)
        {
            await db.Chats
                .Where(c => c.ChatId == selectedChatId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ContactEmail, contact.Email));
            selectedChat.ContactEmail = contact.Email;
            recipients = new List<string> { contact.Email };
            return;
        }

        // 4. Chat name looks like an email address
        if (chatName.Contains('@'))
        {
            await db.Chats
                .Where(c => c.ChatId == selectedChatId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ContactEmail, chatName));
            selectedChat.ContactEmail = chatName;
            recipients = new List<string> { chatName };
            return;
        }

        recipients = new List<string>();
    }

    private string sendError = string.Empty;

    private async Task SendMessage()
    {
        try
        {
        // Read actual DOM value — textarea is uncontrolled (no value="@messageText" binding)
        var domText = await JS.InvokeAsync<string>("getMessageInputValue");
        messageText = domText; // keep shadow field in sync for button state
        if ((string.IsNullOrWhiteSpace(messageText) && pendingAttachments.Count == 0) || selectedChat == null) return;

        var text = messageText.Trim();
        messageText = string.Empty;
        await JS.InvokeVoidAsync("clearMessageInput");

        // ── Edit mode ──────────────────────────────────────────────────
        if (editingMessage != null)
        {
            var editing = editingMessage;
            editingMessage = null;

            // Optimistic: update in-memory immediately
            var inMem = messages?.FirstOrDefault(m => m.MessageId == editing.MessageId);
            if (inMem != null) { inMem.Content = text; inMem.FormattedContent = HtmlFormatter.Format(text); inMem.IsEdited = true; }
            _scrollToBottom = true;
            StateHasChanged();

            await DbContext.Messages
                .Where(m => m.MessageId == editing.MessageId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Content, text)
                    .SetProperty(m => m.FormattedContent, HtmlFormatter.Format(text))
                    .SetProperty(m => m.IsEdited, true)
                    .SetProperty(m => m.EditVersion, editing.EditVersion + 1));

            if (recipients != null && recipients.Any())
            {
                _ = TransportService.SendMessageAsync(new OutgoingMessage
                {
                    MessageId = Guid.NewGuid().ToString(),
                    Content = text,
                    Recipients = recipients,
                    GroupId = selectedChat.Type == ChatType.Group ? selectedChat.GroupId : null,
                    Timestamp = NtpClock.UtcNow,
                    Type = MessageType.Edit,
                    EditOf = editing.MessageId,
                    EditVersion = editing.EditVersion + 1,
                    Tier = BatchTier.Immediate,
                    Subject = SubjectLine
                });
            }
            return;
        }

        // ── Regular / reply mode ────────────────────────────────────────
        if (recipients == null || !recipients.Any())
        {
            sendError = "Cannot determine recipient. Try re-opening this chat.";
            return;
        }
        sendError = string.Empty;

        var msgId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        var replyTo = replyingToMessage;
        replyingToMessage = null;

        var newMsg = new ChatMessage
        {
            MessageId = msgId,
            ChatId = selectedChatId!,
            Sender = UserContext.UserEmail,
            Content = text,
            FormattedContent = HtmlFormatter.Format(text),
            Timestamp = now,
            DisplayTimestamp = now,
            ReceivedAt = now,
            InReplyTo = replyTo?.MessageId,
            Status = MessageStatus.Sending
        };

        // Save pending attachments to disk
        var attachmentsToSend = new List<AttachmentInfo>();
        var attachmentEntities = new List<Attachment>();
        foreach (var att in pendingAttachments)
        {
            var attFileName = $"{msgId}_{att.FileName}";
            var attPath = Path.Combine(DbPathInfo.AttachmentsDir, attFileName);
            Directory.CreateDirectory(DbPathInfo.AttachmentsDir);
            await File.WriteAllBytesAsync(attPath, att.Data);

            attachmentEntities.Add(new Attachment
            {
                Id = Guid.NewGuid().ToString(),
                MessageId = msgId,
                FileName = att.FileName,
                ContentType = att.ContentType,
                Size = att.Size,
                FilePath = attFileName,
                Caption = att.Caption,
            });
            attachmentsToSend.Add(new AttachmentInfo
            {
                FileName = att.FileName,
                ContentType = att.ContentType,
                Size = att.Size,
                Data = att.Data
            });
        }
        pendingAttachments.Clear();

        // ── Optimistic UI: add to in-memory list and render immediately ──
        messages ??= new List<ChatMessage>();
        messages.Add(newMsg);
        if (attachmentEntities.Count > 0)
            _messageAttachments[msgId] = attachmentEntities;
        if (selectedChat != null) selectedChat.LastActivityAt = now;
        _scrollToBottom = true;
        StateHasChanged();

        // ── Persist to DB ──
        DbContext.Messages.Add(newMsg);
        foreach (var ae in attachmentEntities) DbContext.Attachments.Add(ae);
        var trackedChat = await DbContext.Chats.FindAsync(selectedChatId);
        if (trackedChat != null) trackedChat.LastActivityAt = now;

        // Capture and clear the one-time invite token atomically with the message save.
        // Clearing before the send means a retry won't re-use the same token, which is
        // intentional — the token is one-time on Alice's side anyway.
        string? pendingInviteToken = null;
        if (trackedChat != null && !string.IsNullOrEmpty(trackedChat.PendingOutgoingInviteToken))
        {
            pendingInviteToken = trackedChat.PendingOutgoingInviteToken;
            trackedChat.PendingOutgoingInviteToken = null;
            if (selectedChat != null) selectedChat.PendingOutgoingInviteToken = null;
        }

        await DbContext.SaveChangesAsync();

        // ── Send via SMTP in background, then update status ──
        var outgoing = new OutgoingMessage
        {
            MessageId   = msgId,
            Content     = text,
            Recipients  = recipients,
            GroupId     = selectedChat.Type == ChatType.Group ? selectedChat.GroupId : null,
            Timestamp   = now,
            InReplyTo   = replyTo?.MessageId,
            Tier        = BatchTier.Immediate,
            Subject     = SubjectLine,
            Attachments = attachmentsToSend.Count > 0 ? attachmentsToSend : null,
            InviteToken = pendingInviteToken
        };
        _ = TransportService.SendMessageAsync(outgoing).ContinueWith(async t =>
        {
            // Only update status on success — failures are handled by SendSingleAsync
            // which updates the DB before throwing, so we don't overwrite Failed with Sending
            if (!t.IsCompletedSuccessfully) return;

            try
            {
                await DbContext.Messages
                    .Where(m => m.MessageId == msgId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MessageStatus.Sent));
                // Update in-memory status so UI reflects checkmark without full reload
                await InvokeAsync(() =>
                {
                    var msg = messages?.FirstOrDefault(m => m.MessageId == msgId);
                    if (msg != null) { msg.Status = MessageStatus.Sent; StateHasChanged(); }
                });
            }
            catch (Exception ex)
            {
                FileLogger.Write("WARN", "SendMessage", $"Status update failed: {ex.Message}");
            }
        });
        }
        catch (Exception ex)
        {
            FileLogger.Write("ERROR", "SendMessage", $"Send failed: {ex.Message}");
            sendError = "Send failed: " + ex.Message;
            StateHasChanged();
        }
    }

    private async Task HandleReaction((string Emoji, ChatMessage Message) data)
    {
        if (string.IsNullOrEmpty(data.Emoji)) return;

        var msgId = data.Message.MessageId;
        var existing = _messageReactions
            .Where(kvp => kvp.Key == msgId)
            .SelectMany(kvp => kvp.Value)
            .FirstOrDefault(r => r.Emoji == data.Emoji && r.Sender == UserContext.UserEmail);

        // ── Optimistic update: mutate in-memory state and redraw immediately ──
        if (!_messageReactions.TryGetValue(msgId, out var list))
        {
            list = new List<MessageReaction>();
            _messageReactions[msgId] = list;
        }

        MessageReaction? newReaction = null;
        if (existing != null)
        {
            list.Remove(existing);
        }
        else
        {
            newReaction = new MessageReaction
            {
                MessageId = msgId,
                Emoji = data.Emoji,
                Sender = UserContext.UserEmail,
                Timestamp = NtpClock.UtcNow
            };
            list.Add(newReaction);
        }
        StateHasChanged(); // render immediately

        // ── Persist to DB (fast, SQLite) ──
        if (existing != null)
            DbContext.MessageReactions.Remove(existing);
        else if (newReaction != null)
            DbContext.MessageReactions.Add(newReaction);
        await DbContext.SaveChangesAsync();

        // ── Send email in background (slow, SMTP) ──
        if (newReaction != null && recipients != null && recipients.Any())
        {
            var outgoing = new OutgoingMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                Content = data.Emoji,
                Recipients = recipients,
                GroupId = selectedChat?.Type == ChatType.Group ? selectedChat.GroupId : null,
                Timestamp = NtpClock.UtcNow,
                Type = MessageType.Reaction,
                Reaction = data.Emoji,
                ReactionTo = msgId,
                Tier = BatchTier.System
            };
            _ = TransportService.SendMessageAsync(outgoing)
                .ContinueWith(t => FileLogger.Write("WARN", "HandleReaction",
                    $"Send reaction failed: {t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private async Task LoadReactionsAsync()
    {
        if (selectedChatId == null) return;
        var msgIds = messages?.Select(m => m.MessageId).ToList();
        if (msgIds == null || msgIds.Count == 0) return;

        using var scope = ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var reacts = await db.MessageReactions
            .AsNoTracking()
            .Where(r => msgIds.Contains(r.MessageId))
            .ToListAsync();

        _messageReactions = reacts.GroupBy(r => r.MessageId)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    // ── Reply / Edit / Delete actions ────────────────────────────────────

    private void StartReply(ChatMessage msg)
    {
        editingMessage = null;
        replyingToMessage = msg;
    }

    private void StartEdit(ChatMessage msg)
    {
        replyingToMessage = null;
        editingMessage = msg;
        messageText = msg.Content;
        // Push value into the uncontrolled textarea — fire-and-forget is fine here
        _ = JS.InvokeVoidAsync("setMessageInputValue", msg.Content).AsTask();
    }

    private void CancelReply() => replyingToMessage = null;
    private void CancelEdit()
    {
        editingMessage = null;
        messageText = string.Empty;
        _ = JS.InvokeVoidAsync("clearMessageInput").AsTask();
    }

    private async Task DeleteMessageForMe(ChatMessage msg)
    {
        await DbContext.Messages
            .Where(m => m.MessageId == msg.MessageId)
            .ExecuteDeleteAsync();
        await LoadMessagesAsync();
    }

    private async Task DeleteMessageForAll(ChatMessage msg)
    {
        await DbContext.Messages
            .Where(m => m.MessageId == msg.MessageId)
            .ExecuteDeleteAsync();
        await LoadMessagesAsync();

        if (recipients != null && recipients.Any())
        {
            await TransportService.SendMessageAsync(new OutgoingMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                Content = string.Empty,
                Recipients = recipients,
                GroupId = selectedChat?.Type == ChatType.Group ? selectedChat.GroupId : null,
                Timestamp = NtpClock.UtcNow,
                Type = MessageType.Delete,
                DeleteOf = msg.MessageId,
                Tier = BatchTier.Immediate
            });
        }
    }

    private void StartForward(ChatMessage msg)
    {
        var otherChats = chats?.Where(c => c.ChatId != selectedChatId).ToList();
        if (otherChats == null || otherChats.Count == 0) return;
        forwardingMessage = msg;
        showForwardDialog = true;
    }

    private async Task ForwardToChat(Chat targetChat)
    {
        showForwardDialog = false;
        if (forwardingMessage == null) return;

        var msg = forwardingMessage;
        forwardingMessage = null;

        var targetRecipients = await GetRecipientsForChatAsync(targetChat);
        if (targetRecipients.Count == 0) return;

        var msgId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        DbContext.Messages.Add(new ChatMessage
        {
            MessageId = msgId,
            ChatId = targetChat.ChatId,
            Sender = UserContext.UserEmail,
            Content = msg.Content,
            FormattedContent = HtmlFormatter.Format(msg.Content),
            Timestamp = now,
            DisplayTimestamp = now,
            ReceivedAt = now
        });
        var trackedChat = await DbContext.Chats.FindAsync(targetChat.ChatId);
        if (trackedChat != null) trackedChat.LastActivityAt = now;
        await DbContext.SaveChangesAsync();

        await TransportService.SendMessageAsync(new OutgoingMessage
        {
            MessageId = msgId,
            Content = msg.Content,
            Recipients = targetRecipients,
            GroupId = targetChat.Type == ChatType.Group ? targetChat.ChatId : null,
            Timestamp = now,
            Tier = BatchTier.Immediate
        });

        if (selectedChatId == targetChat.ChatId)
        {
            await LoadMessagesAsync();
            _scrollToBottom = true;
        }
        await LoadChatsAsync();
    }

    private async Task<List<string>> GetRecipientsForChatAsync(Chat chat)
    {
        if (chat.Type == ChatType.Group)
        {
            return await DbContext.GroupMembers
                .Where(m => m.GroupId == chat.GroupId)
                .Select(m => m.MemberEmail)
                .ToListAsync();
        }

        var myEmail = UserContext.UserEmail;
        var otherSender = await DbContext.Messages
            .Where(m => m.ChatId == chat.ChatId && m.Sender != myEmail)
            .Select(m => m.Sender)
            .FirstOrDefaultAsync();
        if (otherSender != null) return new List<string> { otherSender };

        var contact = await DbContext.Contacts.FirstOrDefaultAsync(c =>
            c.AccountId == activeAccountId && (c.DisplayName == chat.Name || c.Email == chat.Name));
        if (contact != null) return new List<string> { contact.Email };

        if (chat.Name.Contains('@')) return new List<string> { chat.Name };

        return new List<string>();
    }

    private async Task ScrollToMessage(string messageId)
    {
        // Scroll the messages container to the element with this ID
        try
        {
            await JS.InvokeVoidAsync("eval",
                $"var el=document.getElementById('msg-{messageId}');" +
                "if(el)el.scrollIntoView({behavior:'smooth',block:'center'});");
        }
        catch { }
    }

    private bool IsOwnMessage(ChatMessage message) => message.Sender.Equals(UserContext.UserEmail, StringComparison.OrdinalIgnoreCase);

    private string GetAvatarColorClass(string name)
    {
        var hash = 0;
        foreach (var c in name) hash = (hash * 31 + c) % 16;
        return $"avatar-color-{hash}";
    }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey)
            await SendMessage();
    }

    private async Task OnMessageKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey)
        {
            bool shouldSend = (sendOnEnter && !e.CtrlKey) || (!sendOnEnter && e.CtrlKey);
            FileLogger.Write("DEBUG", "EnterKey", $"key={e.Key}, shift={e.ShiftKey}, ctrl={e.CtrlKey}, sendOnEnter={sendOnEnter}, shouldSend={shouldSend}");
            if (shouldSend)
            {
                await SendMessage();
            }
        }
    }

    private async Task OnMessageTextInput(ChangeEventArgs e)
    {
        var wasEmpty = string.IsNullOrWhiteSpace(messageText);
        messageText = e.Value?.ToString() ?? string.Empty;
        await JS.InvokeVoidAsync("autoResizeTextarea");

        if (wasEmpty != string.IsNullOrWhiteSpace(messageText))
            StateHasChanged(); // send-button enabled state changed — re-render is needed
        else
            _suppressTextareaRender = true; // suppress the automatic post-event re-render Blazor always fires
    }

    private void ToggleEmojiPicker()
    {
        showEmojiPicker = !showEmojiPicker;
        showAttachMenu = false;
    }

    private void InsertEmoji(string emoji)
    {
        messageText += emoji;
        showEmojiPicker = false;
        // Push appended emoji into the uncontrolled textarea
        _ = JS.InvokeVoidAsync("setMessageInputValue", messageText).AsTask();
        StateHasChanged();
    }

    private void ShowAttachMenu()
    {
        showAttachMenu = !showAttachMenu;
        showEmojiPicker = false;
    }

    private void ClosePopups()
    {
        showAttachMenu = false;
        showEmojiPicker = false;
        showFormatMenu = false;
        StateHasChanged();
    }

    private async Task OnTextareaContextMenu()
    {
        selectedText = await JS.InvokeAsync<string>("getSelectedText");
        Console.WriteLine("[OnTextareaContextMenu] Selected text:", selectedText, "len:", selectedText?.Length);
        
        if (!string.IsNullOrEmpty(selectedText))
        {
            var pos = await JS.InvokeAsync<PositionResult>("getLastContextMenuPosition");
            Console.WriteLine("[OnTextareaContextMenu] Position:", pos.x, pos.y);
            
            formatMenuX = pos.x - 60;
            formatMenuY = pos.y - 50;
            showFormatMenu = true;
            StateHasChanged();
        }
        else
        {
            await Task.Delay(50);
            selectedText = await JS.InvokeAsync<string>("getSelectedText");
            Console.WriteLine("[OnTextareaContextMenu] Delayed check - selected:", selectedText, "len:", selectedText?.Length);
            
            if (!string.IsNullOrEmpty(selectedText))
            {
                var pos = await JS.InvokeAsync<PositionResult>("getLastContextMenuPosition");
                formatMenuX = pos.x - 60;
                formatMenuY = pos.y - 50;
                showFormatMenu = true;
                StateHasChanged();
            }
        }
    }

    private class PositionResult { public double x { get; set; } public double y { get; set; } }

    public async Task ShowFormatMenuAt(string inputId, double clientX, double clientY, string text)
    {
        selectedText = text;
        formatMenuX = clientX - 60;
        formatMenuY = clientY - 50;
        showFormatMenu = true;
        await InvokeAsync(StateHasChanged);
    }

    private void CloseFormatMenu()
    {
        showFormatMenu = false;
        selectedText = "";
    }

    private async Task InsertFormat(string prefix, string suffix)
    {
        await JS.InvokeVoidAsync("wrapSelectedText", "messageInput", prefix, suffix);
        var domText = await JS.InvokeAsync<string>("eval", "document.getElementById('messageInput').value");
        messageText = domText;
        showFormatMenu = false;
        StateHasChanged();
    }

    private async Task SelectPhoto()
    {
        showAttachMenu = false;
        await Task.Delay(50); // Allow UI to update
        await JS.InvokeVoidAsync("eval", "document.getElementById('photoInput').click();");
    }

    private async Task SelectFile()
    {
        showAttachMenu = false;
        await Task.Delay(50);
        await JS.InvokeVoidAsync("eval", "document.getElementById('fileInput').click();");
    }

    private async Task OnPhotoSelected(InputFileChangeEventArgs e)
    {
        foreach (var file in e.GetMultipleFiles())
        {
            if (pendingAttachments.Any(a => a.FileName == file.Name && a.Size == file.Size))
                continue;
            using var stream = file.OpenReadStream(25 * 1024 * 1024);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var dataUrl = $"data:{file.ContentType};base64,{Convert.ToBase64String(ms.ToArray())}";
            var isImage = file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            pendingAttachments.Add(new PendingAttachment
            {
                FileName = file.Name,
                ContentType = file.ContentType,
                Size = file.Size,
                Data = ms.ToArray(),
                DataUrl = isImage ? dataUrl : "",
            });
        }
        StateHasChanged();
    }

    private async Task OnFileSelected(InputFileChangeEventArgs e)
    {
        foreach (var file in e.GetMultipleFiles())
        {
            if (pendingAttachments.Any(a => a.FileName == file.Name && a.Size == file.Size))
                continue;
            using var stream = file.OpenReadStream(25 * 1024 * 1024);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            pendingAttachments.Add(new PendingAttachment
            {
                FileName = file.Name,
                ContentType = file.ContentType,
                Size = file.Size,
                Data = ms.ToArray(),
                DataUrl = "",
            });
        }
        StateHasChanged();
    }

    private void ShowChatInfo() => showChatInfo = true;

    private string FormatTime(DateTimeOffset? time)
    {
        if (!time.HasValue) return "";
        var now = DateTimeOffset.Now;
        var diff = now - time.Value;
        var local = time.Value.ToLocalTime();
        if (diff.TotalMinutes < 1) return "now";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m";
        if (diff.TotalDays < 1) return local.ToString("HH:mm");
        if (diff.TotalDays < 7) return local.ToString("ddd");
        return local.ToString("MMM d");
    }

    private static readonly System.Text.RegularExpressions.Regex _urlRegex = new(
        @"(?<!href="")(?<!src="")((?:https?://|www\.)[^\s<>""]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private Microsoft.AspNetCore.Components.MarkupString FormatContentWithLinks(string text)
    {
        if (string.IsNullOrEmpty(text)) return (Microsoft.AspNetCore.Components.MarkupString)"";

        var result = _urlRegex.Replace(text, match =>
        {
            var url = match.Value;
            var href = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
            var display = url.Length > 50 ? url[..47] + "…" : url;
            return $"<a href=\"{href}\" target=\"_blank\" rel=\"noopener noreferrer\" style=\"color:inherit;text-decoration:underline;word-break:break-all;\">{display}</a>";
        });

        return (Microsoft.AspNetCore.Components.MarkupString)result;
    }

private class PendingAttachment
    {
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long Size { get; set; } = 0;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string DataUrl { get; set; } = "";
        public string Caption { get; set; } = "";
    }

    private static ChatList? _instance;
    
    [JSInvokable]
    public static Task OnMobileFormatMenu(double x, double y, string text)
    {
        return _instance?.ShowMobileFormatMenu(x, y, text) ?? Task.CompletedTask;
    }
    
    private Task ShowMobileFormatMenu(double x, double y, string text)
    {
selectedText = text;
        formatMenuX = x;
        formatMenuY = y;
        showFormatMenu = true;
        StateHasChanged();
        return Task.CompletedTask;
    }
}
