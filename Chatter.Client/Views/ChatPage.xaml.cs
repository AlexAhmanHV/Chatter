using Chatter.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Chatter.Client.Views;

public partial class ChatPage : ContentPage
{
    private readonly IServiceProvider _services;

    public ChatPage(ChatViewModel vm, IServiceProvider services)
    {
        InitializeComponent();
        _services = services;           // ✅ store the provider
        BindingContext = vm;
    }

    private async void OnSettingsClicked(object sender, EventArgs e)
    {
        var settings = _services.GetRequiredService<SettingsPage>();
        await Navigation.PushAsync(settings);
    }
}
