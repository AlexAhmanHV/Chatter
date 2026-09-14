namespace Chatter.Shared.Models;

public sealed record RegisterRequest(string Email, string Password, string? DisplayName);
public sealed record LoginRequest(string Email, string Password);

// Returned by both /auth/register and /auth/login. The client stores AccessToken and uses
// DisplayName directly instead of decoding the JWT itself.
public sealed record AuthResponse(string AccessToken, DateTime ExpiresAtUtc, string DisplayName);
