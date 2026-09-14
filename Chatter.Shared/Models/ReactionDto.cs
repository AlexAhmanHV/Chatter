namespace Chatter.Shared.Models;

// Aggregated view of one emoji's reactions on a message, from the requesting caller's
// perspective (ReactedByMe depends on who's asking, so this is never cached/shared as-is).
public sealed record ReactionDto(string Emoji, int Count, bool ReactedByMe);
