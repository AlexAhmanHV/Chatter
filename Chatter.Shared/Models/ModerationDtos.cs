namespace Chatter.Shared.Models;

// One pending report in a site admin's queue - see ChatHub.GetReports/ReportMessage. ChatLabel is
// built for an admin's-eye view (shows both DM participants by name, not "the other person" -
// the admin usually isn't a member of the reported chat, so there's no "my" perspective to label
// it from the way a normal chat list entry is).
public sealed record ReportDto(
    long ReportId,
    long MessageId,
    string ChatId,
    string ChatLabel,
    string MessageSnippet,
    string MessageSenderDisplayName,
    string ReporterDisplayName,
    string Reason,
    DateTime CreatedAtUtc);
