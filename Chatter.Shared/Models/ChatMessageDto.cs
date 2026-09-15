namespace Chatter.Shared.Models;

// Attachment metadata only - the bytes themselves are fetched on demand via
// ChatHub.GetAttachmentData(messageId), not carried here, so opening a chat with a long history
// of images/voice notes doesn't mean downloading every one of them up front. Bundled into a
// single object (rather than separate parameters) because the live broadcast events
// (ReceiveChatMessage/DmNotify) go through SignalR's HubConnection.On<T1..T8>, which tops out at
// 8 type parameters - this keeps room to add fields here without ever hitting that ceiling again.
// DurationSeconds is set only for a voice message (ChatHub.SendVoiceMessage); null for an image.
public sealed record AttachmentMetaDto(
    string FileName,
    string ContentType,
    int SizeBytes,
    int? DurationSeconds);

// A lightweight preview of the message a reply points to - resolved fresh every time (not
// snapshotted at reply time), so it reflects a later edit and turns into IsDeleted if the
// original is later deleted. Snippet is truncated text, or a content-type label ("Photo", "Voice
// message") for an attachment-only original.
public sealed record ReplyPreviewDto(
    long MessageId,
    string Sender,
    string Snippet,
    bool IsDeleted);

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
    IReadOnlyList<ReactionDto> Reactions,
    AttachmentMetaDto? Attachment,
    bool IsForwarded,
    ReplyPreviewDto? ReplyTo);

// The actual bytes of a message's attachment, fetched lazily via ChatHub.GetAttachmentData.
public sealed record AttachmentDataDto(string ContentType, byte[] Data);
