// Views/ChatPage.xaml.cs
using Chatter.Client.ViewModels;

namespace Chatter.Client.Views;

public partial class ChatPage : ContentPage
{
    private readonly IServiceProvider _services;

    public ChatPage(ChatViewModel vm, IServiceProvider services)
    {
        InitializeComponent();
        BindingContext = vm;
        _services = services;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is ChatViewModel vm)
        {
            vm.IsActive = true;
            vm.RefreshVisibleChat(); // ensure middle panel matches buffer & clears unread
        }
    }

    protected override void OnDisappearing()
    {
        if (BindingContext is ChatViewModel vm)
            vm.IsActive = false;
        base.OnDisappearing();
    }

    private async void OnSettingsClicked(object sender, EventArgs e)
    {
        var settings = _services.GetRequiredService<SettingsPage>();
        await Navigation.PushAsync(settings);
    }

    private async void OnOnlineUserSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BindingContext is not ChatViewModel vm) return;

        var picked = e.CurrentSelection?.FirstOrDefault() as string;
        if (!string.IsNullOrWhiteSpace(picked) &&
            !picked.Equals(vm.User, StringComparison.OrdinalIgnoreCase))
        {
            await vm.StartDmCommand.ExecuteAsync(picked);
        }

        if (sender is CollectionView cv) cv.SelectedItem = null;
    }
}
