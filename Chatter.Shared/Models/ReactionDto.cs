namespace Chatter.Shared.Models;

// Aggregated view of one emoji's reactions on a message, from the requesting caller's
// perspective (ReactedByMe depends on who's asking, so this is never cached/shared as-is).
// ReactedBy is display names, for a "who reacted" tooltip - fine at demo scale; a chat with
// thousands of reactions on one message would want a capped/paged version of this instead.
public sealed record ReactionDto(string Emoji, int Count, bool ReactedByMe, IReadOnlyList<string> ReactedBy);
