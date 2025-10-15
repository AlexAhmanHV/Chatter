using Chatter.Client.Views;

namespace Chatter.Client;

public partial class App : Application
{
    public App(ChatPage page)
    {
        InitializeComponent();
        MainPage = new NavigationPage(page);
    }
}
