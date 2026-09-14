/*
File: AttachmentStatusTextConverter.cs

What this does:
- Purpose: Turns ChatMessageItem.IsAttachmentLoading into the placeholder's status line.
- Where used: The attachment placeholder in ChatPage's message bubble template.
*/

using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace Chatter.Client.Converters
{
    public sealed class AttachmentStatusTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? "Loading..." : "Tap to view";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
