/*
File: ChatTextParser.cs

What this does:
- Purpose: Minimal chat text parser that only recognizes emoji shortcodes (e.g., `:smile:`) and converts them to Unicode glyphs.
- How: Scans the input with a single regex for `:shortcode:` tokens, maps known shortcodes via `EmojiMap`
-  and emits the rest as plain text.
*/

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Chatter.Core.Services;

// Only the token types we need for this minimal parser
public enum ChatTokenType { Text, Emoji }

public sealed record ChatToken(ChatTokenType Type, string Value, int Start, int Length)
{
    public override string ToString() => $"{Type}('{Value}' @ {Start}+{Length})";
}

public static class ChatTextParser
{
    // Emoji map (shortcode -> glyph). Add/modify entries here.
    private static readonly Dictionary<string, string> EmojiMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Faces / emotions
        ["smile"] = "😄",
        ["grin"] = "😁",
        ["joy"] = "😂",
        ["rofl"] = "🤣",
        ["wink"] = "😉",
        ["blush"] = "😊",
        ["slight_smile"] = "🙂",
        ["neutral"] = "😐",
        ["thinking"] = "🤔",
        ["sunglasses"] = "😎",
        ["astonished"] = "😲",
        ["scream"] = "😱",
        ["sad"] = "😢",
        ["cry"] = "😢",
        ["sob"] = "😭",
        ["angry"] = "😠",
        ["sleeping"] = "😴",
        ["zipper_mouth"] = "🤐",
        ["mind_blown"] = "🤯",

        // Gestures / hands
        ["thumbsup"] = "👍",
        ["thumbsdown"] = "👎",
        ["+1"] = "👍",
        ["-1"] = "👎",
        ["clap"] = "👏",
        ["wave"] = "👋",
        ["ok_hand"] = "👌",
        ["pray"] = "🙏",
        ["muscle"] = "💪",
        ["pinched_fingers"] = "🤌",

        // Hearts & celebration
        ["heart"] = "❤️",
        ["hearts"] = "💞",
        ["sparkles"] = "✨",
        ["tada"] = "🎉",
        ["party"] = "🥳",
        ["star"] = "⭐",
        ["fire"] = "🔥",
        ["boom"] = "💥",

        // Objects / misc
        ["check"] = "✅",
        ["x"] = "❌",
        ["warning"] = "⚠️",
        ["info"] = "ℹ️",
        ["rocket"] = "🚀",
        ["hourglass"] = "⌛",
        ["paperclip"] = "📎",
        ["link"] = "🔗",
        ["mail"] = "✉️",
        ["calendar"] = "📅",
    };

    // Regex for :shortcode:
    private static readonly TimeSpan RxTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly Regex EmojiRx =
        new(@":([a-z0-9_\-]+):",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RxTimeout);

    // Public API: parse input into Emoji/Text tokens
    public static List<ChatToken> SafeParse(string input)
    {
        input ??= string.Empty;

        var claimed = new bool[input.Length];
        var tokens = new List<ChatToken>();

        foreach (Match m in EmojiRx.Matches(input))
        {
            int s = m.Index, e = s + m.Length;

            for (int i = s; i < e && i < claimed.Length; i++) claimed[i] = true;

            var raw = m.Value;                     // e.g., ":smile:"
            var val = TransformEmoji(raw);         // -> "😄" or original shortcode if unknown
            tokens.Add(new ChatToken(ChatTokenType.Emoji, val, s, m.Length));
        }

        // Emit remaining unclaimed spans as Text tokens
        int idx = 0;
        while (idx < input.Length)
        {
            if (claimed[idx]) { idx++; continue; }
            int start = idx;
            while (idx < input.Length && !claimed[idx]) idx++;
            var text = input.Substring(start, idx - start);
            tokens.Add(new ChatToken(ChatTokenType.Text, text, start, text.Length));
        }

        tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        return tokens;
    }

    // Helper: map ":shortcode:" to glyph, or leave as-is if unknown
    private static string TransformEmoji(string shortcode)
    {
        var key = shortcode.Trim(':');
        return EmojiMap.TryGetValue(key, out var emoji) ? emoji : shortcode;
        // If you prefer to drop unknown shortcodes as plain text instead, return key when not found.
    }
}
