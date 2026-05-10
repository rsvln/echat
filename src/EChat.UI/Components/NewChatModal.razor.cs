using EChat.Core.Crypto;
using EChat.Core.Data;
using EChat.Core.Models;
using EChat.Core.Protocol;
using EChat.Core.Sync;
using EChat.Core.Transport;
using EChat.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Net.Codecrete.QrCodeGenerator;

namespace EChat.UI.Components;

public partial class NewChatModal
{
    [Parameter] public bool IsVisible { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public Account? ActiveAccount { get; set; }
    [Parameter] public string? ActiveAccountId { get; set; }
    [Parameter] public EventCallback<string> OnChatReady { get; set; }

    private string newChatTab = "invite";
    private string newChatEmail = string.Empty;
    private string inviteInput = string.Empty;
    private string inviteCodeInput = string.Empty;
    private string newGroupName = string.Empty;
    private List<string> groupMemberEmails = new() { "" };
    private string newChatError = string.Empty;
    private string copied = string.Empty;
    private List<Contact> allContacts = new();

    private string myInviteCode = string.Empty;
    private string myInviteLink = string.Empty;
    private MarkupString myInviteQr;
    private bool _isGenerating;

    private IEnumerable<Contact> contactsWithKeys =>
        allContacts.Where(c =>
            !string.IsNullOrEmpty(c.PublicKey) &&
            !c.Email.Equals(UserContext.UserEmail, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<Contact> filteredContacts =>
        string.IsNullOrWhiteSpace(newChatEmail)
            ? Enumerable.Empty<Contact>()
            : allContacts.Where(c =>
                c.Email.Contains(newChatEmail, StringComparison.OrdinalIgnoreCase) ||
                (c.DisplayName ?? "").Contains(newChatEmail, StringComparison.OrdinalIgnoreCase));

    private bool _wasVisible;

    protected override async Task OnParametersSetAsync()
    {
        if (IsVisible && !_wasVisible)
        {
            // Modal just opened
            _wasVisible = true;
            await JS.InvokeVoidAsync("lockBodyScroll");

            // Load contacts fresh every time modal opens
            using var scope = ScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            allContacts = await db.Contacts.AsNoTracking()
                .Where(c => c.AccountId == ActiveAccountId)
                .OrderBy(c => c.DisplayName).ToListAsync();
            await BuildMyInviteAsync();
        }
        else if (!IsVisible && _wasVisible)
        {
            // Modal just closed from outside (parent set IsVisible=false)
            _wasVisible = false;
            await JS.InvokeVoidAsync("unlockBodyScroll");
        }
    }

    private async Task HandleClose()
    {
        _wasVisible = false;
        await JS.InvokeVoidAsync("unlockBodyScroll");
        Reset();
        await OnClose.InvokeAsync();
    }

    private void Reset()
    {
        newChatEmail = string.Empty;
        inviteInput = string.Empty;
        inviteCodeInput = string.Empty;
        newGroupName = string.Empty;
        groupMemberEmails = new List<string> { "" };
        newChatError = string.Empty;
        copied = string.Empty;
        newChatTab = "invite";
    }

    private async Task OnOverlayKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
            await HandleClose();
    }

    private void SwitchTab(string tab)
    {
        newChatTab = tab;
        newChatError = string.Empty;
    }

    private async Task RegenerateInviteAsync()
    {
        if (_isGenerating) return;
        copied = string.Empty;
        await BuildMyInviteAsync();
    }

    /// <summary>
    /// Generates a fresh one-time invite token and builds the QR / link.
    /// Called every time the modal opens so each share produces a new token.
    /// </summary>
    private async Task BuildMyInviteAsync()
    {
        if (ActiveAccount == null || string.IsNullOrEmpty(ActiveAccountId)) return;

        _isGenerating = true;
        StateHasChanged();
        try
        {
            var (formattedToken, _) = await InviteService.GenerateAsync(ActiveAccountId);
            myInviteCode = formattedToken;
        }
        catch
        {
            myInviteCode = "?????-?????-?????-?????-?????-?????";
        }

        var tokenRaw  = EChat.Core.Services.InviteService.Normalize(myInviteCode);
        var email     = Uri.EscapeDataString(ActiveAccount.Email);
        var name      = Uri.EscapeDataString(ActiveAccount.DisplayName);
        // pubKey is no longer in the URL — it travels encrypted in the first email
        myInviteLink  = $"echat://invite?e={email}&n={name}&t={tokenRaw}";

        try
        {
            var qr  = QrCode.EncodeText(myInviteLink, QrCode.Ecc.Medium);
            var svg = qr.ToSvgString(3);
            // No inline style — CSS .invite-qr-box svg controls size.
            myInviteQr = new MarkupString(svg);
        }
        catch
        {
            myInviteQr = new MarkupString("<div style='color:#aaa;text-align:center;padding:20px'>QR unavailable</div>");
        }

        _isGenerating = false;
    }

    private Task CopyToClipboard(string text) => CopyToClipboard(text, "link");

    private async Task CopyToClipboard(string text, string what)
    {
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
        }
        catch
        {
            await JS.InvokeVoidAsync("eval",
                $"(function(){{var t=document.createElement('textarea');t.value={System.Text.Json.JsonSerializer.Serialize(text)};document.body.appendChild(t);t.select();document.execCommand('copy');document.body.removeChild(t);}})()");
        }

        copied = what;
        StateHasChanged();
        await Task.Delay(1500);
        copied = string.Empty;
        StateHasChanged();
    }

    private void SelectContact(string email) => newChatEmail = email;

    private async Task OnNewChatKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await AddContactFromInviteOrEmail();
    }

    private async Task OnAddContactKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await AddContactFromInviteOrEmail();
    }

    private async Task AddContactFromInviteOrEmail()
    {
        newChatError = string.Empty;

        if (!string.IsNullOrWhiteSpace(inviteInput))
        {
            await CreateChatFromInvite(inviteInput.Trim());
            return;
        }

        if (!string.IsNullOrWhiteSpace(inviteCodeInput))
        {
            await CreateChatFromCodeAndEmail(inviteCodeInput.Trim(), newChatEmail.Trim());
            return;
        }

        await CreateDirectChat();
    }

    private async Task CreateChatFromInvite(string input)
    {
        if (!input.StartsWith("echat://invite", StringComparison.OrdinalIgnoreCase))
        {
            newChatError = "Expected an echat:// invite link. If you have a code, enter it in the Invite code field below.";
            return;
        }

        string email;
        string displayName;
        string? invitePublicKey;
        string? pendingToken;
        try
        {
            var uri = new Uri(input);
            var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]),
                              StringComparer.OrdinalIgnoreCase);
            email           = pairs.GetValueOrDefault("e", string.Empty);
            displayName     = pairs.GetValueOrDefault("n", string.Empty);
            invitePublicKey = null; // pubKey no longer in URL — arrives encrypted in first email

            // Parse one-time invite token (t=) if present
            var rawToken = pairs.GetValueOrDefault("t");
            pendingToken = string.IsNullOrEmpty(rawToken)
                ? null
                : EChat.Core.Services.InviteService.FormatToken(rawToken.ToUpperInvariant());
        }
        catch
        {
            newChatError = "Invalid invite link.";
            return;
        }

        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            newChatError = "Invite link does not contain a valid email.";
            return;
        }

        await StartOrOpenChat(email, displayName, verified: true, invitePublicKey, pendingToken);
    }

    private async Task CreateChatFromCodeAndEmail(string code, string email)
    {
        // Accept both formatted (with dashes) and plain (no dashes) codes
        var normalizedCode = EChat.Core.Services.InviteService.Normalize(code);
        if (normalizedCode.Length != 30 ||
            !normalizedCode.All(c => char.IsLetterOrDigit(c)))
        {
            newChatError = "Invalid invite code (30 alphanumeric characters expected).";
            return;
        }
        code = normalizedCode; // use normalized form for storage

        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            newChatError = "Enter a valid email address.";
            return;
        }

        if (email.Equals(UserContext.UserEmail, StringComparison.OrdinalIgnoreCase))
        {
            newChatError = "You can't chat with yourself.";
            return;
        }

        using var scope = ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var contact = await db.Contacts.FindAsync(ActiveAccountId, email);
        string? publicKey = contact?.PublicKey;

        var displayName = contact?.DisplayName ?? email.Split('@')[0];
        // The code IS the invite token — store it on the chat so the first message carries it
        await StartOrOpenChat(email, displayName, verified: true, publicKey, pendingInviteToken: code);
    }

    private async Task CreateDirectChat()
    {
        newChatError = string.Empty;
        var email = newChatEmail.Trim();

        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            newChatError = "Enter a valid email address.";
            return;
        }

        if (email.Equals(UserContext.UserEmail, StringComparison.OrdinalIgnoreCase))
        {
            newChatError = "You can't chat with yourself.";
            return;
        }

        await StartOrOpenChat(email, email.Split('@')[0], verified: false, publicKey: null);
    }

    private async Task StartOrOpenChat(string email, string displayName, bool verified,
        string? publicKey = null, string? pendingInviteToken = null)
    {
        var existingChat = await DbContext.Chats
            .Where(c => c.Type == ChatType.OneToOne &&
                        !c.Deleted &&
                        (c.AccountId == ActiveAccountId || c.AccountId == null) &&
                        c.ContactEmail == email)
            .FirstOrDefaultAsync();

        if (existingChat != null)
        {
            if (existingChat.Archived)
                existingChat.Archived = false;

            if (publicKey != null)
            {
                var c = await DbContext.Contacts.FindAsync(ActiveAccountId, email);
                if (c != null && c.PublicKey != publicKey)
                    c.PublicKey = publicKey;
            }

            // Store/update the invite token so the next message carries it
            if (!string.IsNullOrEmpty(pendingInviteToken))
                existingChat.PendingOutgoingInviteToken = pendingInviteToken;

            await DbContext.SaveChangesAsync();
            await HandleClose();
            await OnChatReady.InvokeAsync(existingChat.ChatId);
            return;
        }

        var contact = await DbContext.Contacts.FindAsync(ActiveAccountId, email);
        if (contact == null)
        {
            contact = new Contact
            {
                AccountId   = ActiveAccountId!,
                Email       = email,
                DisplayName = string.IsNullOrEmpty(displayName) ? email.Split('@')[0] : displayName,
                Verified    = verified,
                PublicKey   = publicKey
            };
            DbContext.Contacts.Add(contact);
        }
        else
        {
            if (verified && !contact.Verified)
                contact.Verified = true;
            if (!string.IsNullOrEmpty(displayName))
                contact.DisplayName = displayName;
            if (publicKey != null)
                contact.PublicKey = publicKey;
        }

        var chatName = contact.DisplayName ?? email.Split('@')[0];
        var chatId   = Guid.NewGuid().ToString();
        DbContext.Chats.Add(new Chat
        {
            ChatId                    = chatId,
            Type                      = ChatType.OneToOne,
            Name                      = chatName,
            ContactEmail              = email,
            AccountId                 = ActiveAccountId,
            CreatedAt                 = DateTimeOffset.UtcNow,
            LastActivityAt            = DateTimeOffset.UtcNow,
            PendingOutgoingInviteToken = pendingInviteToken
        });
        await DbContext.SaveChangesAsync();

        await HandleClose();
        await OnChatReady.InvokeAsync(chatId);
    }

    private void AddGroupMember() => groupMemberEmails.Add(string.Empty);

    private void RemoveGroupMember(string email)
    {
        if (groupMemberEmails.Count > 1)
            groupMemberEmails.Remove(email);
    }

    private void UpdateGroupMember(string oldEmail, string newEmail)
    {
        var idx = groupMemberEmails.IndexOf(oldEmail);
        if (idx >= 0)
            groupMemberEmails[idx] = newEmail;
    }

    private async Task CreateGroup()
    {
        newChatError = string.Empty;
        var name = newGroupName.Trim();

        FileLogger.Write("INFO", "CreateGroup", $"Starting: name='{name}', members=[{string.Join(",", groupMemberEmails)}], activeAccountId={ActiveAccountId}");

        if (string.IsNullOrEmpty(name))
        {
            newChatError = "Group name is required.";
            return;
        }

        var validEmails = groupMemberEmails
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        FileLogger.Write("INFO", "CreateGroup", $"validEmails=[{string.Join(",", validEmails)}]");

        if (!validEmails.Any())
        {
            newChatError = "Add at least one member with a verified encryption key.";
            return;
        }

        var chatId = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();

        var groupKeyIdentity = $"{groupId}@echat.local";
        var (groupPubKey, groupPrivKey) = Pgp.GenerateKeyPair(groupKeyIdentity, "");
        var groupFingerprint = Pgp.GetFingerprint(groupPubKey);

        DbContext.Chats.Add(new Chat
        {
            ChatId = chatId,
            Type = ChatType.Group,
            GroupId = groupId,
            Name = name,
            AccountId = ActiveAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow
        });

        DbContext.Groups.Add(new ChatGroup
        {
            GroupId = groupId,
            Name = name,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });

        DbContext.GroupKeyPairs.Add(new GroupKeyPair
        {
            GroupId = groupId,
            PublicKey = groupPubKey,
            PrivateKey = groupPrivKey,
            Fingerprint = groupFingerprint,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Resolve display names for all members (creator + invitees) for the protocol payload
        var myAccount = await DbContext.Accounts.FindAsync(UserContext.AccountId);
        var myDisplayName = myAccount?.DisplayName ?? UserContext.UserEmail;

        // member_names: email → displayName, sent in group-create so recipients know everyone's name
        var memberNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(UserContext.UserEmail))
            memberNameMap[UserContext.UserEmail] = myDisplayName;

        foreach (var email in validEmails)
        {
            if (email.Equals(UserContext.UserEmail, StringComparison.OrdinalIgnoreCase)) continue;
            var c = await DbContext.Contacts.FindAsync(ActiveAccountId, email);
            memberNameMap[email] = c?.DisplayName ?? email;
        }

        if (!string.IsNullOrEmpty(UserContext.UserEmail))
        {
            DbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = groupId,
                MemberEmail = UserContext.UserEmail,
                Role = GroupRole.Admin,
                AddedAt = DateTimeOffset.UtcNow,
                NameColor = GroupPalette.PickColor(UserContext.UserEmail!),
                DisplayName = myDisplayName
            });
        }

        foreach (var email in validEmails)
        {
            if (email.Equals(UserContext.UserEmail, StringComparison.OrdinalIgnoreCase))
                continue;

            DbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = groupId,
                MemberEmail = email,
                Role = GroupRole.Member,
                AddedAt = DateTimeOffset.UtcNow,
                AddedBy = UserContext.UserEmail,
                NameColor = GroupPalette.PickColor(email),
                DisplayName = memberNameMap.GetValueOrDefault(email)
            });
        }

        await DbContext.SaveChangesAsync();
        FileLogger.Write("INFO", "CreateGroup", $"DB saved: chatId={chatId}, groupId={groupId}");

        foreach (var email in validEmails)
        {
            if (email.Equals(UserContext.UserEmail, StringComparison.OrdinalIgnoreCase))
            {
                FileLogger.Write("DEBUG", "CreateGroup", $"Skipping self: {email}");
                continue;
            }

            var contact = await DbContext.Contacts.FindAsync(ActiveAccountId, email);
            FileLogger.Write("INFO", "CreateGroup", $"Contact lookup for {email}: found={contact != null}, hasKey={!string.IsNullOrEmpty(contact?.PublicKey)}");
            if (contact == null || string.IsNullOrEmpty(contact.PublicKey))
            {
                FileLogger.Write("WARN", "CreateGroup", $"Skipping {email}: no contact or no public key");
                continue;
            }

            var allMemberEmails = validEmails.Concat(new[] { UserContext.UserEmail }).Distinct().ToList();
            var groupCreatePayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "group-create",
                group_id = groupId,
                group_name = name,
                version = 1,
                members = allMemberEmails,
                admins = new[] { UserContext.UserEmail },
                group_public_key = groupPubKey,
                group_private_key = groupPrivKey,
                member_names = memberNameMap
            });

            try
            {
                FileLogger.Write("INFO", "CreateGroup", $"Sending group-create to {email}, groupId={groupId}");
                await TransportService.SendMessageAsync(new OutgoingMessage
                {
                    MessageId = Guid.NewGuid().ToString(),
                    Content = groupCreatePayload,
                    Recipients = new List<string> { email },
                    RecipientPublicKey = contact.PublicKey,
                    Timestamp = NtpClock.UtcNow,
                    Type = MessageType.System,
                    SystemType = "group-create",
                    GroupId = groupId,
                    Tier = BatchTier.Immediate,
                    Encrypt = true
                });
                FileLogger.Write("INFO", "CreateGroup", $"SendMessageAsync completed for {email}");
            }
            catch (Exception ex)
            {
                FileLogger.Write("ERROR", "CreateGroup", $"Exception sending to {email}: {ex}");
                newChatError = $"Failed to send group invite to {email}: {ex.Message}";
            }
        }

        await HandleClose();
        await OnChatReady.InvokeAsync(chatId);
    }
}
