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

namespace Chatter.Client.Converters
{
    public sealed class RecordButtonTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? "⏹" : "🎤";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class RecordButtonBackgroundConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? Color.FromArgb("#DC2626") : Color.FromArgb("#1F2937");

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
