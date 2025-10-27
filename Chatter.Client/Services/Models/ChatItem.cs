/*
File: ChatItem.cs

What this does:
- Purpose: View-model model for a chat entry in the left-side chat list (Lobby, DMs, etc.).
- How: Uses CommunityToolkit.Mvvm [ObservableObject]/[ObservableProperty] to auto-generate boilerplate.
- Where used: Bound to the chats CollectionView in ChatPage; selected to load messages for that chat.
*/

using CommunityToolkit.Mvvm.ComponentModel;

namespace Chatter.Client.Models;

public partial class ChatItem : ObservableObject
{
    public ChatItem(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty] public partial string Label { get; set; } = string.Empty;
    [ObservableProperty] public partial int Unread { get; set; }
}