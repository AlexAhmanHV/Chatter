namespace Chatter.Shared.Models;

// Sent by the server for each chat a user belongs to. The server owns chat IDs
// (opaque, based on user IDs) and label resolution (e.g. "who is the other DM
// participant"), since only the server knows which user IDs a display name maps to.
// UnreadCount is derived from the caller's own persisted read receipt (see
// ChatHub.MarkRead) vs. how many messages from other people exist past it, so an
// unread badge survives an app restart instead of only reflecting the current session.
public sealed record ChatSummary(string Id, string Label, int UnreadCount);
