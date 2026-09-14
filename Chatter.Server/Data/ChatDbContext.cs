using Microsoft.EntityFrameworkCore;

namespace Chatter.Server.Data;

// Durable storage for chat history and display names, backed by a local SQLite file.
// A demo-scale replacement for the previous purely in-memory ChatHub state, which lost
// everything on every restart. Schema changes go through EF Core migrations (see Data/Migrations
// and ChatDbContextFactory) - Program.cs runs Database.MigrateAsync() at startup.
//
// To add a migration after changing this model: cd Chatter.Server && dotnet ef migrations add
// <Name> -o Data/Migrations
public class ChatDbContext : DbContext
{
    public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options) { }

    public DbSet<ChatMessageEntity> Messages => Set<ChatMessageEntity>();
    public DbSet<UserProfileEntity> UserProfiles => Set<UserProfileEntity>();
    public DbSet<MessageReactionEntity> Reactions => Set<MessageReactionEntity>();
    public DbSet<ChatEntity> Chats => Set<ChatEntity>();
    public DbSet<ChatMemberEntity> ChatMembers => Set<ChatMemberEntity>();
    public DbSet<ReadReceiptEntity> ReadReceipts => Set<ReadReceiptEntity>();
    public DbSet<BlockedUserEntity> Blocks => Set<BlockedUserEntity>();
    public DbSet<MutedChatEntity> MutedChats => Set<MutedChatEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatMessageEntity>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.ChatId, m.SentAtUtc });
        });

        modelBuilder.Entity<UserProfileEntity>(e =>
        {
            e.HasKey(u => u.UserId);
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
}

public class UserProfileEntity
{
    public required string UserId { get; set; }
    public required string DisplayName { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
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
