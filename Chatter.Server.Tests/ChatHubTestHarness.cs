using System.Collections.Generic;
using System.Security.Claims;
using Chatter.Server.Data;
using Chatter.Server.Hubs;
using Chatter.Server.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Chatter.Server.Tests;

// Builds a ChatHub instance with its SignalR plumbing (Context/Clients/Groups) stubbed out,
// following the pattern Microsoft documents for unit-testing hubs: Hub.Context/.Clients/.Groups
// are public and settable specifically so tests don't need a live connection.
// https://learn.microsoft.com/aspnet/core/signalr/hubs#unit-testing-hubs
public static class ChatHubTestHarness
{
    // One shared in-memory database per test *method* (unique db name), so tests don't see
    // each other's rows. ChatHub's own dictionaries are static or process-wide by design, so
    // callers should still use unique (Guid-based) user ids / display names per test to avoid
    // interference from other tests running in the same process.
    public static ChatDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new ChatDbContext(options);
    }

    private sealed class FakeDbContextFactory(string dbName) : IDbContextFactory<ChatDbContext>
    {
        public ChatDbContext CreateDbContext() => CreateDb(dbName);

        public Task<ChatDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public FakeHubCallerContext(string connectionId, string? userId, bool isAdmin = false)
        {
            ConnectionId = connectionId;
            User = userId is null
                ? new ClaimsPrincipal(new ClaimsIdentity())
                : new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("sub", userId), new Claim("is_admin", isAdmin ? "true" : "false") }, "Test"));
        }

        public bool Aborted { get; private set; }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal User { get; }
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;
    }

    // connectionId defaults to a fresh id per call: ChatHub's rate limiter (and other
    // per-connection state) is keyed by connection id in static, process-wide dictionaries,
    // so reusing a fixed literal like "conn-1" across tests would leak state between them.
    public static ChatHub Create(string dbName, string? userId, string? connectionId = null, bool isAdmin = false,
        Dictionary<string, string?>? extraConfig = null)
    {
        connectionId ??= Guid.NewGuid().ToString("N");

        // ChatHub keys most of its own state off the JWT's "sub" claim alone, but a few methods
        // (UpdateAvatar, PersistDisplayNameAsync) look up a real ApplicationUser row via
        // db.Users - seed one so those aren't testing against an account that doesn't exist.
        if (userId is not null) EnsureUserRowExists(dbName, userId, isAdmin);

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var singleProxy = new Mock<ISingleClientProxy>();
        singleProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubCallerClients>();
        clients.SetupGet(c => c.Caller).Returns(singleProxy.Object);
        clients.SetupGet(c => c.All).Returns(proxy.Object);
        clients.SetupGet(c => c.Others).Returns(proxy.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        clients.Setup(c => c.OthersInGroup(It.IsAny<string>())).Returns(proxy.Object);
        clients.Setup(c => c.Client(It.IsAny<string>())).Returns(singleProxy.Object);
        clients.Setup(c => c.Clients(It.IsAny<IReadOnlyList<string>>())).Returns(proxy.Object);

        var groups = new Mock<IGroupManager>();
        groups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        groups.Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = new ChatHub(new FakeDbContextFactory(dbName), new LinkPreviewFetcher(new HttpClient()), BuildJwtConfig(extraConfig))
        {
            Context = new FakeHubCallerContext(connectionId, userId, isAdmin),
            Clients = clients.Object,
            Groups = groups.Object,
        };

        return hub;
    }

    // Always has Jwt:SigningKey (ChatHub reads it to sign avatar URLs - see AvatarUrlSigner);
    // extraConfig layers on top for tests that need e.g. Turn:Url/Turn:SharedSecret configured
    // (see ChatHub.GetIceServers).
    private static IConfiguration BuildJwtConfig(Dictionary<string, string?>? extraConfig = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "test-signing-key-at-least-32-bytes-long-for-hmac",
        };
        if (extraConfig is not null)
            foreach (var kv in extraConfig) values[kv.Key] = kv.Value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static void EnsureUserRowExists(string dbName, string userId, bool isAdmin = false)
    {
        using var db = CreateDb(dbName);
        var existing = db.Users.FirstOrDefault(u => u.Id == userId);
        if (existing is not null)
        {
            if (existing.IsAdmin != isAdmin) { existing.IsAdmin = isAdmin; db.SaveChanges(); }
            return;
        }

        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"{userId}@test.local",
            Email = $"{userId}@test.local",
            DisplayName = string.Empty,
            IsAdmin = isAdmin,
        });
        db.SaveChanges();
    }
}
