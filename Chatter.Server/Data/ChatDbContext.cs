using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Chatter.Server.Data;

// Durable storage for chat history, display names, and (via IdentityDbContext) accounts/password
// hashes, backed by a local SQLite file. A demo-scale replacement for the previous purely
// in-memory ChatHub state, which lost everything on every restart. Schema changes go through EF
// Core migrations (see Data/Migrations and ChatDbContextFactory) - Program.cs runs
// Database.MigrateAsync() at startup.
//
// To add a migration after changing this model: cd Chatter.Server && dotnet ef migrations add
// <Name> -o Data/Migrations
//
// Display names live directly on ApplicationUser (Identity's Users table) rather than a separate
// profile table - there's no external identity provider anymore, so this app's own user record
// and Identity's user record are the same row.
public class ChatDbContext : IdentityDbContext<ApplicationUser>
{
    public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options) { }

    public DbSet<ChatMessageEntity> Messages => Set<ChatMessageEntity>();
    public DbSet<MessageReactionEntity> Reactions => Set<MessageReactionEntity>();
    public DbSet<ChatEntity> Chats => Set<ChatEntity>();
    public DbSet<ChatMemberEntity> ChatMembers => Set<ChatMemberEntity>();
    public DbSet<ReadReceiptEntity> ReadReceipts => Set<ReadReceiptEntity>();
    public DbSet<BlockedUserEntity> Blocks => Set<BlockedUserEntity>();
    public DbSet<MutedChatEntity> MutedChats => Set<MutedChatEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // Identity's own tables (Users, Roles, Claims, ...)
        OnChatModelCreating(modelBuilder);
    }

    private static void OnChatModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatMessageEntity>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.ChatId, m.SentAtUtc });
        });

        modelBuilder.Entity<MessageReactionEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.MessageId, r.UserId, r.Emoji }).IsUnique();
        });

        modelBuilder.Entity<ChatEntity>(e =>
        {
            e.HasKey(c => c.ChatId);
        });

        modelBuilder.Entity<ChatMemberEntity>(e =>
        {
            e.HasKey(m => new { m.ChatId, m.UserId });
        });

        modelBuilder.Entity<ReadReceiptEntity>(e =>
        {
            e.HasKey(r => new { r.ChatId, r.UserId });
        });

        modelBuilder.Entity<BlockedUserEntity>(e =>
        {
            e.HasKey(b => new { b.BlockerUserId, b.BlockedUserId });
        });

        modelBuilder.Entity<MutedChatEntity>(e =>
        {
            e.HasKey(m => new { m.UserId, m.ChatId });
        });
    }
}

public class ChatMessageEntity
{
    public long Id { get; set; }
    public required string ChatId { get; set; }
    public required string SenderUserId { get; set; }
    public required string SenderDisplayName { get; set; }
    public required string Body { get; set; }
    public DateTime SentAtUtc { get; set; }
    public DateTime? EditedAtUtc { get; set; }
    public bool IsDeleted { get; set; }

    // An optional image attachment, stored as a blob in the same database as everything else
    // (no external storage - see ChatHub.SendAttachment for the size/content-type limits this
    // is validated against before it's ever saved). Null on a plain text message.
    public string? AttachmentFileName { get; set; }
    public string? AttachmentContentType { get; set; }
    public byte[]? AttachmentData { get; set; }
    public int? AttachmentSizeBytes { get; set; }

    // Set only for a voice message (see ChatHub.SendVoiceMessage); null for an image attachment
    // or a plain text message.
    public int? AttachmentDurationSeconds { get; set; }

    // Set when this message was created via ChatHub.ForwardMessage rather than typed/sent
    // directly - lets the client label it "Forwarded" without guessing from content.
    public bool IsForwarded { get; set; }

    // Set when this message was sent as a reply to another one in the same chat (see
    // ChatHub.BuildReplyPreviewAsync, which validates the two are in the same chat before this
    // is ever written). A forward never carries this over - a forward is a fresh message, not
    // part of the original's reply chain.
    public long? ReplyToMessageId { get; set; }
}

public class MessageReactionEntity
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public required string UserId { get; set; }
    public required string Emoji { get; set; }
}

// Metadata for a named group chat. Lobby and DMs don't need a row here: Lobby is implicit and
// DMs derive their two participants directly from the chat id (see ChatHub.MakeDmId).
public class ChatEntity
{
    public required string ChatId { get; set; }
    public required string Name { get; set; }
    public required string CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

// Persisted membership for group chats (again, not needed for Lobby/DMs).
public class ChatMemberEntity
{
    public required string ChatId { get; set; }
    public required string UserId { get; set; }

    // Group admins can add/remove members and rename the group (see ChatHub.RequireGroupAdmin).
    // The creator starts as the sole admin; PromoteGroupAdmin/DemoteGroupAdmin can add or remove
    // others, and the last remaining admin is auto-succeeded on leave so a group is never
    // orphaned. Meaningless for Lobby/DM rows, which don't use this table's admin concept.
    public bool IsAdmin { get; set; }
}

public class ReadReceiptEntity
{
    public required string ChatId { get; set; }
    public required string UserId { get; set; }
    public long LastReadMessageId { get; set; }
    public DateTime LastReadAtUtc { get; set; }
}

// A -> B: A has blocked B. Blocking is one-directional to record but enforced both ways (see
// ChatHub.IsBlockedEitherWay) - if either party has blocked the other, they can't DM each other.
public class BlockedUserEntity
{
    public required string BlockerUserId { get; set; }
    public required string BlockedUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

// Per-user, per-chat mute: messages still arrive normally, this only suppresses the unread
// badge/notification locally - the other party is never told and delivery is unaffected.
public class MutedChatEntity
{
    public required string UserId { get; set; }
    public required string ChatId { get; set; }
    public DateTime MutedAtUtc { get; set; }
}
