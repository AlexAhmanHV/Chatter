using System.Text;
using Chatter.Server; // so Program.cs can see ChatHub
using Chatter.Server.Auth;
using Chatter.Server.Data;
using Chatter.Server.Hubs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

// Chat history + display names now live in SQLite instead of only in memory, so a
// server restart no longer wipes every conversation.
var connectionString = builder.Configuration.GetConnectionString("Chatter") ?? "Data Source=chatter.db";
builder.Services.AddDbContextFactory<ChatDbContext>(opt => opt.UseSqlite(connectionString));

// ----- Authentication: verify the Supabase-issued JWT on every hub connection -----
var supabaseUrl = builder.Configuration["Supabase:Url"]
    ?? throw new InvalidOperationException(
        "Missing configuration value 'Supabase:Url'. Set it in appsettings.json or via the SUPABASE__URL environment variable.");
var supabaseAudience = builder.Configuration["Supabase:Audience"] ?? "authenticated";

// Optional: only needed for older Supabase projects still using the legacy shared
// HS256 secret (Project Settings -> API -> JWT Settings -> Legacy JWT secret).
// Newer projects sign with rotating asymmetric keys published at the JWKS endpoint below,
// which needs no secret on this server at all.
var legacyJwtSecret = builder.Configuration["Supabase:JwtSecret"];

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"{supabaseUrl}/auth/v1",
            ValidateAudience = true,
            ValidAudience = supabaseAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        };

        if (!string.IsNullOrWhiteSpace(legacyJwtSecret))
        {
            validationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(legacyJwtSecret));
        }
        else
        {
            var jwksAddress = $"{supabaseUrl}/auth/v1/.well-known/jwks.json";
            var configManager = new ConfigurationManager<JsonWebKeySet>(
                jwksAddress, new SupabaseJwksRetriever(), new HttpDocumentRetriever());

            validationParameters.IssuerSigningKeyResolver = (_, _, kid, _) =>
            {
                var jwks = configManager.GetConfigurationAsync(CancellationToken.None).GetAwaiter().GetResult();
                return kid is null ? jwks.Keys : jwks.Keys.Where(k => k.Kid == kid);
            };
        }

        options.TokenValidationParameters = validationParameters;

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

    var profiles = await db.UserProfiles.ToListAsync();
    ChatHub.PreloadDisplayNames(profiles);

    var groupChats = await db.Chats.ToListAsync();
    var groupMembers = await db.ChatMembers.ToListAsync();
    ChatHub.PreloadGroupChats(groupChats, groupMembers);
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

// SignalR hub endpoint (requires a valid Supabase JWT — see ChatHub's [Authorize] attribute)
app.MapHub<ChatHub>("/hub/Chat");

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
