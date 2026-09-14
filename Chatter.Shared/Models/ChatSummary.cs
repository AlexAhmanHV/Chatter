namespace Chatter.Shared.Models;

// Sent by the server for each chat a user belongs to. The server owns chat IDs
// (opaque, based on user IDs) and label resolution (e.g. "who is the other DM
// participant"), since only the server knows which user IDs a display name maps to.
public sealed record ChatSummary(string Id, string Label);
