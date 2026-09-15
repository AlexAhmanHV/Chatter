namespace Chatter.Shared.Models;

// Rich-preview metadata for a URL found in a message body, fetched server-side (see
// ChatHub.GetLinkPreview / Services/LinkPreviewFetcher) rather than by the client directly, so
// every client doesn't need to make its own outbound request to an arbitrary URL a peer typed.
// Title/Description/ImageUrl are all independently nullable - a page might only have some of them.
public sealed record LinkPreviewDto(string Url, string? Title, string? Description, string? ImageUrl);
