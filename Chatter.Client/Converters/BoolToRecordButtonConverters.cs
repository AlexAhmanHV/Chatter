/*
File: BoolToRecordButtonConverters.cs

What this does:
- Purpose: Toggle the composer's record button between its idle and recording appearance.
- Where used: ChatPage's composer, bound to ChatViewModel.IsRecordingVoiceMessage.
*/

using System;
using System.Globalization;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using UraniumUI.Icons.MaterialSymbols;

namespace Chatter.Client.Converters
{
    public sealed class RecordButtonTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? MaterialOutlined.Stop : MaterialOutlined.Mic;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class RecordButtonBackgroundConverter : IValueConverter
    {
        // Mirrors Theme.xaml's ColorError/ColorSurfaceVariant - can't look those up from
        // Application.Current.Resources here since Theme.xaml is deliberately not merged at the
        // App level (see App.xaml's comment on the native-crash workaround).
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? Color.FromArgb("#DC2626") : Color.FromArgb("#0D1524");

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
