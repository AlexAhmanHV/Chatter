// Chatter.Core/Services/ChatTextParser.cs
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Chatter.Core.Services;

public enum ChatTokenType { Text, Mention, Command, Emoji, Url, Hashtag }

public sealed record ChatToken(ChatTokenType Type, string Value, int Start, int Length)
{
    public override string ToString() => $"{Type}('{Value}' @ {Start}+{Length})";
}

public static class ChatTextParser
{
    // Minimal emoji map – extend as needed
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

    // Regexes
    private static readonly Regex UrlRx =
        new(@"(?:(?:https?|ftp)://)[^\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MentionRx =
        new(@"(?<=^|\s)@([A-Za-z0-9_\-\.]{1,32})", RegexOptions.Compiled);

    private static readonly Regex HashtagRx =
        new(@"(?<=^|\s)#([A-Za-z0-9_\-]{1,50})", RegexOptions.Compiled);

    private static readonly Regex EmojiRx =
        new(@":([a-z0-9_\-]+):", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CommandRx =
        new(@"^/([a-z]+)(?:\s+(.*))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Entry point: safe, single-call parser.
    /// </summary>
    public static List<ChatToken> SafeParse(string input)
    {
        input ??= string.Empty;

        // Whole-line command takes the whole message
        var cmdMatch = CommandRx.Match(input);
        if (cmdMatch.Success)
            return new List<ChatToken> { new(ChatTokenType.Command, input.Trim(), 0, input.Length) };

        var claimed = new bool[input.Length];
        var tokens = new List<ChatToken>();

        // Helper to add matches while preventing overlaps
        void AddMatches(Regex rx, ChatTokenType type, Func<string, string> transform)
        {
            foreach (Match m in rx.Matches(input))
            {
                int s = m.Index, e = s + m.Length;
                bool overlaps = false;
                for (int i = s; i < e; i++) { if (i < claimed.Length && claimed[i]) { overlaps = true; break; } }
                if (overlaps) continue;

                for (int i = s; i < e && i < claimed.Length; i++) claimed[i] = true;

                var raw = m.Value;
                var val = type switch
                {
                    ChatTokenType.Emoji => TransformEmoji(raw),
                    ChatTokenType.Mention or ChatTokenType.Hashtag => raw.Trim(),
                    _ => raw
                };

                tokens.Add(new ChatToken(type, val, s, m.Length));
            }
        }

        // Priority: URLs first, then mentions/hashtags/emoji
        AddMatches(UrlRx, ChatTokenType.Url, s => s);
        AddMatches(MentionRx, ChatTokenType.Mention, s => s.Trim());
        AddMatches(HashtagRx, ChatTokenType.Hashtag, s => s.Trim());
        AddMatches(EmojiRx, ChatTokenType.Emoji, TransformEmoji);

        // Fill remaining as Text
        int idx = 0;
        while (idx < input.Length)
        {
            if (idx < claimed.Length && claimed[idx]) { idx++; continue; }
            int start = idx;
            while (idx < input.Length && (idx >= claimed.Length || !claimed[idx])) idx++;
            var text = input.Substring(start, idx - start);
            tokens.Add(new ChatToken(ChatTokenType.Text, text, start, text.Length));
        }

        tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        return tokens;
    }

    private static string TransformEmoji(string shortcode)
    {
        var key = shortcode.Trim(':');
        return EmojiMap.TryGetValue(key, out var emoji) ? emoji : shortcode;
    }
}
