/*
File: ChatMessageItem.cs

What this does:
- Purpose: View-model model for a single rendered chat message - sender, body, timestamp, edit/delete
  state, and reactions. Replaces the earlier flat "Sender: body" string used for chat history.
- How: Uses CommunityToolkit.Mvvm [ObservableObject]/[ObservableProperty]. Id is 0 for synthetic
  lines (rename/system notices) that were never a real persisted message on the server.
- Where used: Bound to the message CollectionView in ChatPage; created/mutated by ChatViewModel in
  response to ChatService events (ReceiveChatMessage, MessageEdited, MessageDeleted, ReactionChanged).
*/

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Chatter.Client.Models;

public partial class ChatMessageItem : ObservableObject
{
    public ChatMessageItem(long id, string sender, string body, DateTime sentAtUtc, bool isMine, bool isSystem)
    {
        Id = id;
        Sender = sender;
        Body = body;
        SentAtUtc = sentAtUtc;
        IsMine = isMine;
        IsSystem = isSystem;
    }

    public long Id { get; }
    public string Sender { get; }
    public DateTime SentAtUtc { get; }
    public bool IsMine { get; }
    public bool IsSystem { get; }

    // Editable/deletable only make sense for the caller's own, real (non-synthetic) messages -
    // the server re-checks ownership independently, this just drives what the UI offers.
    public bool CanModify => IsMine && Id > 0 && !IsSystem;

    [ObservableProperty] public partial string Body { get; set; } = string.Empty;
    [ObservableProperty] public partial DateTime? EditedAtUtc { get; set; }
    [ObservableProperty] public partial bool IsDeleted { get; set; }
    [ObservableProperty] public partial bool SeenByOther { get; set; }

    public bool IsEdited => EditedAtUtc.HasValue;

    public ObservableCollection<ReactionItem> Reactions { get; } = new();
}

public partial class ReactionItem : ObservableObject
{
    private readonly List<string> _reactorNames;

    public ReactionItem(long messageId, string emoji, int count, bool reactedByMe, IEnumerable<string> reactorNames)
    {
        MessageId = messageId;
        Emoji = emoji;
        Count = count;
        ReactedByMe = reactedByMe;
        _reactorNames = reactorNames.ToList();
        ReactorsText = string.Join(", ", _reactorNames);
    }

    // Carried on the reaction (not just its parent message) so a tap on a reaction pill in the
    // UI can bind straight to this object and still know which message to toggle it on.
    public long MessageId { get; }
    public string Emoji { get; }

    [ObservableProperty] public partial int Count { get; set; }
    [ObservableProperty] public partial bool ReactedByMe { get; set; }

    // Bound to a tooltip on the reaction pill ("who reacted"). Maintained incrementally from
    // live ReactionChanged events rather than round-tripping to the server on every toggle.
    [ObservableProperty] public partial string ReactorsText { get; set; } = string.Empty;

    public void AddReactor(string displayName)
    {
        if (!_reactorNames.Contains(displayName, System.StringComparer.OrdinalIgnoreCase))
            _reactorNames.Add(displayName);
        ReactorsText = string.Join(", ", _reactorNames);
    }

    public void RemoveReactor(string displayName)
    {
        _reactorNames.RemoveAll(n => string.Equals(n, displayName, System.StringComparison.OrdinalIgnoreCase));
        ReactorsText = string.Join(", ", _reactorNames);
    }
}
