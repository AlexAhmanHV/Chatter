// Messages/DisplayNameChangedMessage.cs
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Chatter.Client.Messages;

public sealed class DisplayNameChangedMessage : ValueChangedMessage<string>
{
    public DisplayNameChangedMessage(string value) : base(value) { }
}
