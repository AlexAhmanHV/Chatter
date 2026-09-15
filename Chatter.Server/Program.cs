using System.Text;
using System.Threading.RateLimiting;
using Chatter.Server; // so Program.cs can see ChatHub
using Chatter.Server.Auth;
using Chatter.Server.Data;
using Chatter.Server.Hubs;
using Chatter.Shared.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

// Chat history, display names, and (via Identity) accounts all live in SQLite instead of only
// in memory, so a server restart no longer wipes every conversation or logs everyone out.
var connectionString = builder.Configuration.GetConnectionString("Chatter") ?? "Data Source=chatter.db";
builder.Services.AddDbContextFactory<ChatDbContext>(opt => opt.UseSqlite(connectionString));
// Identity's stores (and minimal API model binding) want a plain scoped DbContext, not just the
// factory above. Registering AddDbContext *and* AddDbContextFactory separately for the same
// context conflicts (each configures its own DbContextOptions pipeline) - this is the documented
// workaround: derive the scoped context from the same factory instead of reconfiguring it.
builder.Services.AddScoped<ChatDbContext>(sp => sp.GetRequiredService<IDbContextFactory<ChatDbContext>>().CreateDbContext());

// ----- Accounts: ASP.NET Core Identity, no external identity provider -----
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // Relaxed for a demo app; a real deployment should keep Identity's stronger defaults.
        options.Password.RequiredLength = 6;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireDigit = false;
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<ChatDbContext>();

// ----- Authentication: verify the server's own JWT (issued by Auth/JwtIssuer) on every
// hub connection and every /auth/* call that needs one -----
//
// If Jwt:SigningKey isn't explicitly configured, generate one and persist it next to the SQLite
// database (same directory, same "survives restarts" volume in the Docker case) rather than
// failing outright - this is what makes `dotnet run`/`docker compose up` work with zero manual
// setup for local/first-time use. Explicitly setting Jwt__SigningKey (as docker-compose.yml's
// template still does) always wins and skips this entirely - a real deployment should keep doing
// that rather than rely on a file quietly written to disk.
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"]
    ?? JwtSigningKeyResolver.ResolveOrGenerate(connectionString);
builder.Configuration["Jwt:SigningKey"] = jwtSigningKey; // so JwtIssuer's injected IConfiguration sees it too
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "ChatterServer";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "ChatterClient";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.MapInboundClaims = false; // keep "sub"/"email"/"display_name" exactly as issued

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
        };

        // Browser/.NET SignalR clients can't set an Authorization header on the WebSocket
        // handshake, so the access token travels as a query parameter instead.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hub/chat"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Login/register are plain HTTP endpoints (unlike hub methods), so ASP.NET Core's built-in
// rate limiting middleware actually applies here - basic brute-force protection.
// Partitioned per client IP: AddFixedWindowLimiter alone would create a single global bucket
// shared by every caller, so one person hammering /auth/login would lock out everyone else too.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

// (Optional) CORS if you’ll test from a browser origin
builder.Services.AddCors(opt =>
{
    opt.AddDefaultPolicy(p => p
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        .WithOrigins(
            "http://localhost", "https://localhost",
            "http://localhost:5173", "https://localhost:7043"));
});

var app = builder.Build();

// Create the SQLite file/schema if it doesn't exist yet, and warm up ChatHub's
// in-memory display-name cache from what was persisted last run.
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ChatDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();

    var users = await db.Users.ToListAsync();
    ChatHub.PreloadDisplayNames(users);

    var groupChats = await db.Chats.ToListAsync();
    var groupMembers = await db.ChatMembers.ToListAsync();
    ChatHub.PreloadGroupChats(groupChats, groupMembers);

    var blocks = await db.Blocks.ToListAsync();
    ChatHub.PreloadBlocks(blocks);

    var mutedChats = await db.MutedChats.ToListAsync();
    ChatHub.PreloadMutedChats(mutedChats);
}

// Pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ----- Auth endpoints -----
app.MapPost("/auth/register", async (RegisterRequest req, UserManager<ApplicationUser> userManager, IConfiguration config) =>
{
    if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Email and password are required." });

    var displayName = string.IsNullOrWhiteSpace(req.DisplayName)
        ? req.Email.Split('@')[0]
        : req.DisplayName.Trim();

    var user = new ApplicationUser { UserName = req.Email, Email = req.Email, DisplayName = displayName };
    var result = await userManager.CreateAsync(user, req.Password);
    if (!result.Succeeded)
        return Results.BadRequest(new { error = string.Join(" ", result.Errors.Select(e => e.Description)) });

    var (token, expiresAtUtc) = JwtIssuer.CreateToken(user, config);
    return Results.Ok(new AuthResponse(token, expiresAtUtc, user.DisplayName));
})
.RequireRateLimiting("auth");

app.MapPost("/auth/login", async (LoginRequest req, UserManager<ApplicationUser> userManager, IConfiguration config) =>
{
    var user = string.IsNullOrWhiteSpace(req.Email) ? null : await userManager.FindByEmailAsync(req.Email);
    if (user is null || !await userManager.CheckPasswordAsync(user, req.Password ?? string.Empty))
        return Results.Json(new { error = "Invalid email or password." }, statusCode: StatusCodes.Status401Unauthorized);

    var (token, expiresAtUtc) = JwtIssuer.CreateToken(user, config);
    return Results.Ok(new AuthResponse(token, expiresAtUtc, user.DisplayName));
})
.RequireRateLimiting("auth");

// SignalR hub endpoint (requires a valid JWT from /auth/login or /auth/register - see ChatHub's [Authorize])
app.MapHub<ChatHub>("/hub/Chat");

// Public, unauthenticated by design: the simplest way for a plain <Image Source="url"/> in the
// MAUI client to work without wiring an Authorization header through image loading. A profile
// picture isn't sensitive chat content, so this is a deliberate tradeoff, not an oversight - see
// the README's known-simplifications list. Chat message attachments are NOT served this way;
// those stay behind ChatHub.GetAttachmentData's membership check.
app.MapGet("/avatars/{userId}", async (string userId, IDbContextFactory<ChatDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var user = await db.Users.FindAsync(userId);
    if (user?.AvatarData is null || user.AvatarContentType is null)
        return Results.NotFound();

    return Results.File(user.AvatarData, user.AvatarContentType);
});

// Your sample endpoint unchanged
var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild",
    "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        )).ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

// ---- Types ----

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

// Exposes the top-level Program for WebApplicationFactory<Program> in Chatter.Server.Tests.
public partial class Program { }
