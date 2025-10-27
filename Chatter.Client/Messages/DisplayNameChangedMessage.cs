/*
File: DisplayNameChangedMessage.cs

What this does:
- Purpose: Strongly-typed message used to broadcast that the local user's display name has changed.
- How: Inherits from CommunityToolkit.Mvvm.Messaging.Messages.ValueChangedMessage<string> to carry the new name.
- Where used: Published by UI/view-model when the user renames; subscribers (e.g., ChatViewModel) update state/UI.
*/

using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Chatter.Client.Messages;

public sealed class DisplayNameChangedMessage : ValueChangedMessage<string>
{
    public DisplayNameChangedMessage(string value) : base(value) { }
}
