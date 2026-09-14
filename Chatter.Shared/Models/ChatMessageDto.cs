namespace Chatter.Shared.Models;

// A single persisted chat message, returned by ChatHub.GetChatHistory when a client
// opens a chat and needs to backfill messages sent before it connected. Id is the message's
// stable database key - used to target edits, deletes, and reactions at a specific message.
//
// Attachment* fields are metadata only - the image bytes themselves are fetched on demand via
// ChatHub.GetAttachmentData(Id), not included here, so opening a chat with a long history of
// images doesn't mean downloading every one of them up front.
public sealed record ChatMessageDto(
    long Id,
    string Sender,
    string Body,
    DateTime SentAtUtc,
    DateTime? EditedAtUtc,
    bool IsDeleted,
    IReadOnlyList<ReactionDto> Reactions,
    string? AttachmentFileName,
    string? AttachmentContentType,
    int? AttachmentSizeBytes);

// The actual bytes of a message's attachment, fetched lazily via ChatHub.GetAttachmentData.
public sealed record AttachmentDataDto(string ContentType, byte[] Data);
