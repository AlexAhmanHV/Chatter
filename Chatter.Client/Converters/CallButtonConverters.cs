/*
File: CallButtonConverters.cs

What this does:
- Purpose: Toggle the mute/camera button glyphs between their on/off states.
- Where used: CallPage's control bar, bound to CallViewModel.IsMuted/IsCameraOff.
*/

using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace Chatter.Client.Converters
{
    public sealed class MuteButtonTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? "🔇" : "🎤";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class CameraButtonTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? "📷🚫" : "📷";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
