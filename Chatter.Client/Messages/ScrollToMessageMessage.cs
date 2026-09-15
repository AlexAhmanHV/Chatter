/*
File: ScrollToMessageMessage.cs

What this does:
- Purpose: Asks the currently visible ChatPage to scroll its message list to a specific item.
- How: Inherits from CommunityToolkit.Mvvm.Messaging.Messages.ValueChangedMessage<ChatMessageItem>.
- Where used: Published by ChatViewModel.JumpToSearchResultAsync after closing search; ChatPage's
  code-behind (which owns the CollectionView reference) is the subscriber.
*/

using CommunityToolkit.Mvvm.Messaging.Messages;
using Chatter.Client.Models;

namespace Chatter.Client.Messages;

public sealed class ScrollToMessageMessage : ValueChangedMessage<ChatMessageItem>
{
    public ScrollToMessageMessage(ChatMessageItem value) : base(value) { }
}
