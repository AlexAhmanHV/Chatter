/*
File: InvertedBoolConverter.cs

What this does:
- Purpose: Flips a bool for IsVisible bindings (e.g. "show this unless the message is deleted").
- Where used: ChatPage message bubbles (real content vs. "message deleted" placeholder, etc.).
*/

using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace Chatter.Client.Converters
{
    public sealed class InvertedBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            !(value is bool b && b);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            !(value is bool b && b);
    }
}
