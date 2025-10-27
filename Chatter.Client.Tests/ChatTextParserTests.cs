/*
File: ChatTextParserTests.cs

What this does:
- Purpose: Focused unit tests for ChatTextParser.SafeParse covering the emoji shortcode feature we added.
- How: Uses xUnit to verify that known shortcodes map to real emoji, unknown shortcodes fall back to their literal text,
  and mixed content preserves order. Also checks a few practical edge cases (aliases like :+1:, adjacent shortcodes, and
  regular colons that must NOT be treated as emoji).
- Notes: Update expectations if you change EmojiCatalog (e.g., add/remove mappings or tweak aliases).
*/

using System.Linq;
using Chatter.Core.Services;
using Xunit;

namespace Chatter.Client.Tests;

public class ChatTextParserTests
{
    [Fact]
    public void Emoji_KnownShortcode_MapsToEmoji()
    {
        var tokens = ChatTextParser.SafeParse("hello :smile:");
        var joined = string.Concat(tokens.Select(t => t.Value));
        Assert.Contains("😄", joined);
        Assert.EndsWith("😄", joined);
        Assert.Contains(tokens, t => t.Type == ChatTokenType.Emoji && t.Value == "😄");
    }

    [Fact]
    public void Emoji_UnknownShortcode_FallsBackToLiteral()
    {
        var tokens = ChatTextParser.SafeParse("say :does_not_exist:");
        var joined = string.Concat(tokens.Select(t => t.Value));
        Assert.Contains(":does_not_exist:", joined);                 // stays as-is
        Assert.Contains(tokens, t => t.Type == ChatTokenType.Emoji); // still an Emoji token
    }

    [Fact]
    public void Emoji_MixedText_PreservesOrder()
    {
        var input = "gg :party: then :smile:!";
        var tokens = ChatTextParser.SafeParse(input);
        var joined = string.Concat(tokens.Select(t => t.Value));
        Assert.Equal("gg 🥳 then 😄!", joined);
        Assert.Equal(ChatTokenType.Emoji, tokens.First(t => t.Value == "🥳").Type);
        Assert.Equal(ChatTokenType.Emoji, tokens.First(t => t.Value == "😄").Type);
    }

    [Fact]
    public void Emoji_Alias_Forms_Work()
    {
        var tokens = ChatTextParser.SafeParse("nice :+1: not nice :-1:");
        var joined = string.Concat(tokens.Select(t => t.Value));
        Assert.Contains("👍", joined);
        Assert.Contains("👎", joined);
    }

    [Fact]
    public void Emoji_AdjacentShortcodes_ComposeCorrectly()
    {
        var tokens = ChatTextParser.SafeParse(":smile::party::fire:");
        var joined = string.Concat(tokens.Select(t => t.Value));
        Assert.Equal("😄🥳🔥", joined);
    }

    [Fact]
    public void Emoji_DoesNotConvert_NormalColons()
    {
        var tokens = ChatTextParser.SafeParse("ratio 1:2 keep :smile:");
        var joined = string.Concat(tokens.Select(t => t.Value));
        Assert.Contains("1:2", joined); // untouched
        Assert.EndsWith("😄", joined);  // emoji still works after plain colons
    }
}
