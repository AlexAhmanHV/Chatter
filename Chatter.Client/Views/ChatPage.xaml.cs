using Chatter.Client.ViewModels;

namespace Chatter.Client.Views;

public partial class ChatPage : ContentPage
{
    public ChatPage(ChatViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
