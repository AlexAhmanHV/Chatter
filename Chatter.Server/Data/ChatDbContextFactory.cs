using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Chatter.Server.Data;

// Used only by `dotnet ef migrations ...`/`dotnet ef database update` at design time, so the
// CLI doesn't need to spin up the whole app (JWT signing key config, Kestrel) just to inspect
// the model. The connection string here is never used to actually run the app.
public class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseSqlite("Data Source=chatter.db")
            .Options;

        return new ChatDbContext(options);
    }
}
