using Chatter.Client.Services;

namespace Chatter.Client.Views;

public partial class EmojiHelpPage : ContentPage
{
    public EmojiHelpPage()
    {
        InitializeComponent();
        BindingContext = EmojiCatalog.All;
    }
    private async void OnClose(object sender, EventArgs e) => await Navigation.PopModalAsync();
}
