using Microsoft.EntityFrameworkCore;

namespace Chatter.Server.Data;

// Durable storage for chat history and display names, backed by a local SQLite file.
// A demo-scale replacement for the previous purely in-memory ChatHub state, which lost
// everything on every restart. Uses EnsureCreated (no migrations) since the schema is
// small and stable; a production app would use EF Core migrations instead.
public class ChatDbContext : DbContext
{
    public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options) { }

    public DbSet<ChatMessageEntity> Messages => Set<ChatMessageEntity>();
    public DbSet<UserProfileEntity> UserProfiles => Set<UserProfileEntity>();

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
}

public class UserProfileEntity
{
    public required string UserId { get; set; }
    public required string DisplayName { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
