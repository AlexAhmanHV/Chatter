using CommunityToolkit.Mvvm.ComponentModel;

namespace Chatter.Client.Models;

public partial class UserPresenceItem : ObservableObject
{
    public UserPresenceItem(string name, bool isOnline)
    {
        Name = name;
        this.isOnline = isOnline;
    }

    public string Name { get; }

    [ObservableProperty]
    private bool isOnline;
}
