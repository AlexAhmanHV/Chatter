/*
File: PinSwipeItemTextConverter.cs

What this does:
- Purpose: Labels a message's swipe-to-pin action "Pin" or "Unpin" depending on current state.
- Where used: The pin SwipeItem in ChatPage's message bubble template.
*/

using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace Chatter.Client.Converters
{
    public sealed class PinSwipeItemTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? "Unpin" : "Pin";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
