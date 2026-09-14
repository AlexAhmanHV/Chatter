namespace Chatter.Shared.Models;

// A single persisted chat message, returned by ChatHub.GetChatHistory when a client
// opens a chat and needs to backfill messages sent before it connected.
public sealed record ChatMessageDto(string Sender, string Body, DateTime SentAtUtc);
