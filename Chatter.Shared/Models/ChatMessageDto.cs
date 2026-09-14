namespace Chatter.Shared.Models;

// A single persisted chat message, returned by ChatHub.GetChatHistory when a client
// opens a chat and needs to backfill messages sent before it connected. Id is the message's
// stable database key - used to target edits, deletes, and reactions at a specific message.
public sealed record ChatMessageDto(
    long Id,
    string Sender,
    string Body,
    DateTime SentAtUtc,
    DateTime? EditedAtUtc,
    bool IsDeleted,
    IReadOnlyList<ReactionDto> Reactions);
