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
using Microsoft.Maui.Controls;
using Chatter.Shared.Models;

namespace Chatter.Client.Models;

public partial class ChatMessageItem : ObservableObject
{
    public ChatMessageItem(long id, string sender, string body, DateTime sentAtUtc, bool isMine, bool isSystem,
        AttachmentMetaDto? attachment = null, bool isForwarded = false, ReplyPreviewDto? replyTo = null, bool isPinned = false)
    {
        Id = id;
        Sender = sender;
        Body = body;
        SentAtUtc = sentAtUtc;
        IsMine = isMine;
        IsSystem = isSystem;
        Attachment = attachment;
        IsForwarded = isForwarded;
        ReplyTo = replyTo;
        IsPinned = isPinned;
        PlaybackDurationSeconds = attachment?.DurationSeconds ?? 0;
    }

    public long Id { get; }
    public string Sender { get; }
    public DateTime SentAtUtc { get; }
    public bool IsMine { get; }
    public bool IsSystem { get; }
    public bool IsForwarded { get; }

    // Resolved fresh by the server on every fetch/broadcast (see ReplyPreviewDto) - null for a
    // message that isn't a reply.
    public ReplyPreviewDto? ReplyTo { get; }
    public bool HasReplyPreview => ReplyTo is not null;

    // Editable/deletable only make sense for the caller's own, real (non-synthetic) messages -
    // the server re-checks ownership independently, this just drives what the UI offers.
    public bool CanModify => IsMine && Id > 0 && !IsSystem;

    // Forwarding, replying, pinning, and viewing an attachment all need a real persisted message
    // id to reference server-side - never a synthetic system line.
    public bool CanForward => Id > 0 && !IsSystem;
    public bool CanReply => Id > 0 && !IsSystem;
    public bool CanPin => Id > 0 && !IsSystem && !IsDeleted;
    public bool CanViewEditHistory => Id > 0 && !IsSystem && IsEdited;

    [ObservableProperty] public partial bool IsPinned { get; set; }

    [ObservableProperty] public partial string Body { get; set; } = string.Empty;
    [ObservableProperty] public partial DateTime? EditedAtUtc { get; set; }
    [ObservableProperty] public partial bool IsDeleted { get; set; }
    partial void OnIsDeletedChanged(bool value) => OnPropertyChanged(nameof(CanPin));

    // DM-only "Seen" marker - exactly one other person, so a plain bool is enough there.
    [ObservableProperty] public partial bool SeenByOther { get; set; }

    // Group-only equivalent - a group has more than one other member, so "seen" is a count
    // instead of a bool (see ChatViewModel.RecomputeGroupSeenCounts). Null until computed.
    [ObservableProperty] public partial int? SeenCount { get; set; }
    [ObservableProperty] public partial int? SeenTotal { get; set; }
    public bool ShowGroupSeenIndicator => SeenTotal is > 0;
    public string GroupSeenText => $"Seen by {SeenCount}/{SeenTotal}";

    partial void OnSeenCountChanged(int? value) => OnPropertyChanged(nameof(GroupSeenText));
    partial void OnSeenTotalChanged(int? value)
    {
        OnPropertyChanged(nameof(ShowGroupSeenIndicator));
        OnPropertyChanged(nameof(GroupSeenText));
    }

    public bool IsEdited => EditedAtUtc.HasValue;

    // Metadata only - the bytes are fetched on demand (see ChatViewModel.ViewAttachmentAsync)
    // and cached here (as ImageSource for a photo, or a playable stream for a voice message)
    // once loaded, so scrolling a long history doesn't mean downloading every attachment up front.
    public AttachmentMetaDto? Attachment { get; }
    public bool HasAttachment => Attachment is not null;
    public bool IsVoiceMessage => Attachment?.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsVideoAttachment => Attachment?.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsImageAttachment => HasAttachment && !IsVoiceMessage && !IsVideoAttachment;
    public string VoiceMessageDurationText => Attachment?.DurationSeconds is { } s
        ? TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
        : string.Empty;
    public string VideoDurationText => IsVideoAttachment && Attachment?.DurationSeconds is { } vs
        ? TimeSpan.FromSeconds(vs).ToString(vs >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
        : string.Empty;

    [ObservableProperty] public partial ImageSource? AttachmentImage { get; set; }
    [ObservableProperty] public partial bool IsAttachmentLoading { get; set; }
    [ObservableProperty] public partial byte[]? VoiceMessageData { get; set; }
    [ObservableProperty] public partial bool IsPlayingVoiceMessage { get; set; }

    // Set once a video's bytes have been fetched and written to a cache file (see
    // ChatViewModel.PlayVideoAsync), so tapping the same video again reopens it without
    // re-downloading. Path, not bytes - it's handed straight to Launcher.OpenAsync.
    [ObservableProperty] public partial string? VideoLocalPath { get; set; }

    // Playback progress for a voice message, updated on a short timer while playing (see
    // ChatViewModel.PlayVoiceMessageAsync) - PlaybackDurationSeconds defaults to the duration the
    // sender reported, but is corrected once the player itself knows the real one.
    [ObservableProperty] public partial double PlaybackPositionSeconds { get; set; }
    [ObservableProperty] public partial double PlaybackDurationSeconds { get; set; }

    public double PlaybackProgress => PlaybackDurationSeconds > 0
        ? Math.Clamp(PlaybackPositionSeconds / PlaybackDurationSeconds, 0, 1)
        : 0;

    public string PlaybackPositionText => FormatSeconds(PlaybackPositionSeconds) + " / " + FormatSeconds(PlaybackDurationSeconds);

    private static string FormatSeconds(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
    }

    partial void OnPlaybackPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PlaybackProgress));
        OnPropertyChanged(nameof(PlaybackPositionText));
    }

    partial void OnPlaybackDurationSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PlaybackProgress));
        OnPropertyChanged(nameof(PlaybackPositionText));
    }

    // Drives which of the two image-attachment visuals (tap-to-view placeholder vs. loaded
    // image) is shown; re-evaluated whenever AttachmentImage changes (see the generated partial
    // hook below). A voice message uses IsAttachmentLoading/VoiceMessageData directly instead -
    // it always renders as a play button, never a placeholder-vs-image toggle.
    public bool ShowAttachmentPlaceholder => IsImageAttachment && AttachmentImage is null;
    public bool ShowAttachmentImage => IsImageAttachment && AttachmentImage is not null;

    partial void OnAttachmentImageChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(ShowAttachmentPlaceholder));
        OnPropertyChanged(nameof(ShowAttachmentImage));
    }

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
