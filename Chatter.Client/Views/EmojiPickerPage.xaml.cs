using Chatter.Client.Services;
using Chatter.Client.ViewModels;

namespace Chatter.Client.Views;

public partial class EmojiPickerPage : ContentPage
{
    private readonly ChatViewModel _vm;
    private List<EmojiItem> _all = new();

    public EmojiPickerPage(ChatViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _all = EmojiCatalog.All;
        EmojiList.ItemsSource = _all;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var q = e.NewTextValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(q))
        {
            EmojiList.ItemsSource = _all;
            return;
        }

        q = q.ToLowerInvariant();
        EmojiList.ItemsSource = _all.Where(x =>
            x.Shortcode.ToLowerInvariant().Contains(q) ||
            x.Emoji.Contains(q)).ToList();
    }

    private async void OnPick(object sender, TappedEventArgs e)
    {
        if (e.Parameter is EmojiItem item)
        {
            var cur = _vm.OutgoingMessage ?? string.Empty;
            if (cur.Length > 0 && !char.IsWhiteSpace(cur[^1])) cur += " ";
            _vm.OutgoingMessage = cur + item.Shortcode + " ";
            await Navigation.PopModalAsync();
        }
    }

    private async void OnClose(object sender, EventArgs e) => await Navigation.PopModalAsync();
}
