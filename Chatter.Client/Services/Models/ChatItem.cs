using CommunityToolkit.Mvvm.ComponentModel;

namespace Chatter.Client.Models;

public partial class ChatItem : ObservableObject
{
    public ChatItem(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty] private int unread;
}
