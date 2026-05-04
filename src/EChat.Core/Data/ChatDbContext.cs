using EChat.Core.Models;
using EChat.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace EChat.Core.Data;

public class ChatDbContext : DbContext
{
    private readonly ICredentialProtector _protector;

    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<ChatGroup> Groups => Set<ChatGroup>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<GroupKeyPair> GroupKeyPairs => Set<GroupKeyPair>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<ImapFolderSyncState> ImapFolderStates => Set<ImapFolderSyncState>();
    
    /// <param name="protector">
    /// Optional credential protector injected by the DI container.
    /// When null (e.g. during EF design-time migrations) the no-op plaintext protector is used.
    /// </param>
    public ChatDbContext(DbContextOptions<ChatDbContext> options, ICredentialProtector? protector = null)
        : base(options)
    {
        _protector = protector ?? PlaintextCredentialProtector.Instance;
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // ChatMessage
        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.MessageId, e.ChatId }).IsUnique();
            entity.HasIndex(e => e.MessageId);
            entity.HasIndex(e => new { e.ChatId, e.Timestamp });
            entity.HasIndex(e => e.Sender);
            entity.HasOne(e => e.Chat)
                  .WithMany(c => c.Messages)
                  .HasForeignKey(e => e.ChatId);
        });
        
        // Chat
        modelBuilder.Entity<Chat>(entity =>
        {
            entity.HasKey(e => e.ChatId);
            entity.HasIndex(e => e.LastActivityAt);
            entity.HasIndex(e => new { e.Archived, e.LastActivityAt });
            entity.HasIndex(e => e.AccountId);
            entity.HasIndex(e => new { e.AccountId, e.ContactEmail });
            entity.HasIndex(e => e.GroupId);

            entity.HasOne(e => e.Contact)
                  .WithMany()
                  .HasForeignKey(e => e.ContactEmail)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Group)
                  .WithMany()
                  .HasForeignKey(e => e.GroupId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // Account
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(e => e.AccountId);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasIndex(e => e.IsActive);

            // Encrypt sensitive fields at rest using the platform credential protector.
            // With PlaintextCredentialProtector (default) these are no-ops — no behaviour
            // change until a real protector (e.g. DpapiCredentialProtector) is registered.
            // Migration-safe: Unprotect() transparently handles legacy plaintext values
            // that were stored before encryption was introduced.
            var p = _protector;
            entity.Property(e => e.Password)
                .HasConversion(
                    v => p.Protect(v),
                    v => p.Unprotect(v));
            entity.Property(e => e.PrivateKey)
                .HasConversion(
                    v => v == null ? null : p.Protect(v),
                    v => v == null ? null : p.Unprotect(v));
        });
        
        // Contact
        modelBuilder.Entity<Contact>(entity =>
        {
            entity.HasKey(e => e.Email);
            entity.HasIndex(e => e.Verified);
        });
        
        // ChatGroup
        modelBuilder.Entity<ChatGroup>(entity =>
        {
            entity.HasKey(e => e.GroupId);
            entity.HasIndex(e => e.Version);
        });
        
        // GroupMember
        modelBuilder.Entity<GroupMember>(entity =>
        {
            entity.HasKey(e => new { e.GroupId, e.MemberEmail });
            entity.HasOne(e => e.Group)
                  .WithMany(g => g.Members)
                  .HasForeignKey(e => e.GroupId);
            entity.HasIndex(e => e.MemberEmail);
        });
        
        // Setting
        modelBuilder.Entity<Setting>(entity =>
        {
            entity.HasKey(e => e.Key);
        });

        // GroupKeyPair
        modelBuilder.Entity<GroupKeyPair>(entity =>
        {
            entity.HasKey(e => e.GroupId);
            entity.HasIndex(e => e.Fingerprint);
        });

        // Attachment
        modelBuilder.Entity<Attachment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MessageId);
        });

        // MessageReaction
        modelBuilder.Entity<MessageReaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MessageId);
            entity.HasIndex(e => new { e.MessageId, e.Emoji, e.Sender });
        });

        // ImapFolderSyncState
        modelBuilder.Entity<ImapFolderSyncState>(entity =>
        {
            entity.HasKey(e => new { e.AccountId, e.FolderName });
        });
    }
}