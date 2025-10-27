/*
File: EmojiDisplayConverter.cs

What this does:
- Purpose: For UI display, turns :shortcode: sequences in chat text into real emoji.
- How: Uses Chatter.Core.Services.ChatTextParser.SafeParse(...) to tokenize the text, then concatenates token values;
       shortcode tokens are already mapped to their emoji.
- Where used: In ChatPage message bubbles via Converter={StaticResource EmojiDisplay}.
*/

using System;
using System.Globalization;
using System.Linq;
using Microsoft.Maui.Controls;
using Chatter.Core.Services;

namespace Chatter.Client.Converters
{
    public sealed class EmojiDisplayConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var text = value as string ?? string.Empty;
            var tokens = ChatTextParser.SafeParse(text);
            return string.Concat(tokens.Select(t => t.Value));
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value as string ?? string.Empty;
        }
    }
}
