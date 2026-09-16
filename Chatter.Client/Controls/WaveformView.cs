/*
File: WaveformView.cs

What this does:
- Purpose: Draws a voice message's waveform bars (see WavWaveformExtractor), with the played
  portion (up to ChatMessageItem.PlaybackProgress) colored differently from the rest.
- How: A GraphicsView that is its own IDrawable. Since a GraphicsView doesn't repaint on its own
  just because a bound property changed, this subscribes to the assigned ChatMessageItem's
  PropertyChanged directly and calls Invalidate() when WaveformBars or playback progress changes -
  unsubscribing from the previous item first, so a CollectionView recycling this view into a new
  cell doesn't leak the subscription or keep redrawing for a message it no longer represents.
- Where used: The voice-message bubble in ChatPage.xaml (bound via MessageItem="{Binding .}").
*/

using System.ComponentModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Chatter.Client.Models;

namespace Chatter.Client.Controls;

public class WaveformView : GraphicsView, IDrawable
{
    public static readonly BindableProperty MessageItemProperty = BindableProperty.Create(
        nameof(MessageItem), typeof(ChatMessageItem), typeof(WaveformView), null,
        propertyChanged: OnMessageItemChanged);

    public ChatMessageItem? MessageItem
    {
        get => (ChatMessageItem?)GetValue(MessageItemProperty);
        set => SetValue(MessageItemProperty, value);
    }

    public WaveformView() => Drawable = this;

    private static void OnMessageItemChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (WaveformView)bindable;
        if (oldValue is ChatMessageItem oldItem) oldItem.PropertyChanged -= view.OnItemPropertyChanged;
        if (newValue is ChatMessageItem newItem) newItem.PropertyChanged += view.OnItemPropertyChanged;
        view.Invalidate();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessageItem.WaveformBars)
            or nameof(ChatMessageItem.PlaybackProgress)
            or nameof(ChatMessageItem.IsPlayingVoiceMessage))
        {
            Invalidate();
        }
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var bars = MessageItem?.WaveformBars;
        if (bars is null || bars.Length == 0 || dirtyRect.Width <= 0) return;

        var progress = MessageItem!.PlaybackProgress;
        var playedBars = (int)Math.Round(bars.Length * progress);
        var barWidth = dirtyRect.Width / bars.Length;

        for (int i = 0; i < bars.Length; i++)
        {
            var barHeight = (float)Math.Max(2, bars[i] * dirtyRect.Height);
            var x = dirtyRect.Left + i * barWidth;
            var y = dirtyRect.Top + (dirtyRect.Height - barHeight) / 2;
            var width = (float)Math.Max(1, barWidth - 2);

            canvas.FillColor = i < playedBars ? Color.FromArgb("#6EE7F9") : Color.FromArgb("#374151");
            canvas.FillRoundedRectangle(x, y, width, barHeight, 1);
        }
    }
}
