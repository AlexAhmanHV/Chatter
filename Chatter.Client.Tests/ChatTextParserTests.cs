using System.Linq;
using Chatter.Core.Services;
using Xunit;

namespace Chatter.Client.Tests;

public class ChatTextParserTests
{
    [Fact]
    public void Parses_Command_WholeLine()
    {
        var tokens = ChatTextParser.SafeParse("/giphy happy cat");
        Assert.Single(tokens);
        Assert.Equal(ChatTokenType.Command, tokens[0].Type);
        Assert.Equal("/giphy happy cat", tokens[0].Value);
    }

    [Fact]
    public void Parses_Mentions_And_Text()
    {
        var tokens = ChatTextParser.SafeParse("Hi @Alex, meet @Sam!");
        Assert.Equal(5, tokens.Count); // "Hi ", "@Alex", ", meet ", "@Sam", "!"
        Assert.Contains(tokens, t => t.Type == ChatTokenType.Mention && t.Value == "@Alex");
        Assert.Contains(tokens, t => t.Type == ChatTokenType.Mention && t.Value == "@Sam");
    }

    [Fact]
    public void Parses_Emoji_Shortcodes_With_Fallback()
    {
        var tokens = ChatTextParser.SafeParse("gg :smile: and :unknown:");
        var emotes = tokens.Where(t => t.Type == ChatTokenType.Emoji).Select(t => t.Value).ToArray();
        Assert.Contains("😄", emotes);         // mapped
        Assert.Contains(":unknown:", emotes);  // fallback stays shortcode
    }

    [Fact]
    public void Parses_Urls_Without_Overlapping_Others()
    {
        var tokens = ChatTextParser.SafeParse("See https://example.com/@alex#bio now");
        // URL should be a single token
        Assert.Contains(tokens, t => t.Type == ChatTokenType.Url && t.Value.StartsWith("https://example.com/"));
        // Ensure no mention or hashtag created inside URL
        Assert.DoesNotContain(tokens, t => t.Type == ChatTokenType.Mention);
        Assert.DoesNotContain(tokens, t => t.Type == ChatTokenType.Hashtag);
    }

    [Fact]
    public void Parses_Hashtags()
    {
        var tokens = ChatTextParser.SafeParse("Working on #maui #dotnet9");
        // Expected tokens: "Working on " (Text), "#maui" (Hashtag), " " (Text), "#dotnet9" (Hashtag)
        Assert.Equal(4, tokens.Count);
        Assert.Contains(tokens, t => t.Type == ChatTokenType.Hashtag && t.Value == "#maui");
        Assert.Contains(tokens, t => t.Type == ChatTokenType.Hashtag && t.Value == "#dotnet9");
    }

    [Fact]
    public void Mixed_All_Tokens_In_Order()
    {
        var input = "@A check :party: at https://x.y #fun";
        var tokens = ChatTextParser.SafeParse(input);
        var ordered = string.Concat(tokens.Select(t => t.Value));

        // Emoji shortcode is transformed to actual emoji 🥳 by the parser
        Assert.Equal("@A check 🥳 at https://x.y #fun", ordered);

        Assert.Equal(ChatTokenType.Mention, tokens[0].Type);
        Assert.Equal(ChatTokenType.Emoji, tokens[2].Type);
        Assert.Contains(tokens, t => t.Type == ChatTokenType.Url);
        Assert.EndsWith("#fun", ordered);
    }
}
