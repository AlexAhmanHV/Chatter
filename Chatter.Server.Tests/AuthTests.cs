using System.IdentityModel.Tokens.Jwt;
using Chatter.Server.Auth;
using Chatter.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chatter.Server.Tests;

// Covers the two pieces that replaced Supabase: password-based accounts (via ASP.NET Core
// Identity's UserManager, the same type Program.cs's /auth endpoints use) and the server's own
// JWT issuance (Auth/JwtIssuer). This exercises real Identity + EF Core, not a hand-rolled stub,
// so it needs its own minimal DI container rather than the ChatHubTestHarness used elsewhere.
public class AuthTests
{
    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ServiceProvider BuildServices(string dbName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ChatDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 6;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireDigit = false;
                o.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<ChatDbContext>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Register_ThenLogin_PasswordCheckSucceeds()
    {
        using var services = BuildServices(NewId());
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        var email = $"{NewId()}@example.com";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Alice" };
        var created = await userManager.CreateAsync(user, "password1");
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));

        var found = await userManager.FindByEmailAsync(email);
        Assert.NotNull(found);
        Assert.True(await userManager.CheckPasswordAsync(found!, "password1"));
        Assert.False(await userManager.CheckPasswordAsync(found!, "wrong-password"));
    }

    [Fact]
    public async Task Register_DuplicateEmail_Fails()
    {
        using var services = BuildServices(NewId());
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        var email = $"{NewId()}@example.com";
        var first = new ApplicationUser { UserName = email, Email = email, DisplayName = "Alice" };
        Assert.True((await userManager.CreateAsync(first, "password1")).Succeeded);

        var second = new ApplicationUser { UserName = email, Email = email, DisplayName = "Someone Else" };
        var result = await userManager.CreateAsync(second, "password2");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Register_TooShortPassword_Fails()
    {
        using var services = BuildServices(NewId());
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        var email = $"{NewId()}@example.com";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Alice" };
        var result = await userManager.CreateAsync(user, "abc"); // shorter than RequiredLength = 6

        Assert.False(result.Succeeded);
    }

    private static IConfiguration BuildJwtConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "unit-test-signing-key-at-least-32-bytes-long!!",
            ["Jwt:Issuer"] = "TestIssuer",
            ["Jwt:Audience"] = "TestAudience",
        }).Build();

    [Fact]
    public void JwtIssuer_CreateToken_CarriesExpectedClaims()
    {
        var user = new ApplicationUser
        {
            Id = NewId(),
            Email = "alice@example.com",
            DisplayName = "Alice",
        };

        var (token, expiresAtUtc) = JwtIssuer.CreateToken(user, BuildJwtConfig());

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(expiresAtUtc > DateTime.UtcNow);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(user.Id, jwt.Claims.Single(c => c.Type == "sub").Value);
        Assert.Equal("alice@example.com", jwt.Claims.Single(c => c.Type == "email").Value);
        Assert.Equal("Alice", jwt.Claims.Single(c => c.Type == "display_name").Value);
        Assert.Equal("TestIssuer", jwt.Issuer);
        Assert.Equal("TestAudience", jwt.Audiences.Single());
    }

    [Fact]
    public void JwtIssuer_CreateToken_MissingSigningKey_Throws()
    {
        var user = new ApplicationUser { Id = NewId(), Email = "a@b.com", DisplayName = "A" };
        var emptyConfig = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() => JwtIssuer.CreateToken(user, emptyConfig));
    }
}
