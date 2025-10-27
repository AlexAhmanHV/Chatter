namespace Chatter.Client.Services;

public record EmojiItem(string Shortcode, string Emoji);

public static class EmojiCatalog
{
    public static readonly List<EmojiItem> All = new()
    {
        // Faces / emotions
        new(":smile:", "😄"),
        new(":grin:", "😁"),
        new(":joy:", "😂"),
        new(":rofl:", "🤣"),
        new(":wink:", "😉"),
        new(":blush:", "😊"),
        new(":slight_smile:", "🙂"),
        new(":neutral:", "😐"),
        new(":thinking:", "🤔"),
        new(":sunglasses:", "😎"),
        new(":astonished:", "😲"),
        new(":scream:", "😱"),
        new(":sad:", "😢"),
        new(":cry:", "😢"),
        new(":sob:", "😭"),
        new(":angry:", "😠"),
        new(":sleeping:", "😴"),
        new(":zipper_mouth:", "🤐"),
        new(":mind_blown:", "🤯"),

        // Gestures / hands
        new(":thumbsup:", "👍"),
        new(":thumbsdown:", "👎"),
        new(":+1:", "👍"),
        new(":-1:", "👎"),
        new(":clap:", "👏"),
        new(":wave:", "👋"),
        new(":ok_hand:", "👌"),
        new(":pray:", "🙏"),
        new(":muscle:", "💪"),
        new(":pinched_fingers:", "🤌"),

        // Hearts & celebration
        new(":heart:", "❤️"),
        new(":hearts:", "💞"),
        new(":sparkles:", "✨"),
        new(":tada:", "🎉"),
        new(":party:", "🥳"),
        new(":star:", "⭐"),
        new(":fire:", "🔥"),
        new(":boom:", "💥"),

        // Objects / misc
        new(":check:", "✅"),
        new(":x:", "❌"),
        new(":warning:", "⚠️"),
        new(":info:", "ℹ️"),
        new(":rocket:", "🚀"),
        new(":hourglass:", "⌛"),
        new(":paperclip:", "📎"),
        new(":link:", "🔗"),
        new(":mail:", "✉️"),
        new(":calendar:", "📅"),
    };
}
