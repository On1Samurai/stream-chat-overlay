using System.Text;
using System.Text.RegularExpressions;

namespace StreamChat.Core.Emotes;

/// <summary>Делит сообщение на текст и картинки.</summary>
public static partial class MessageContent
{
    public const int MaxImages = 40;

    [GeneratedRegex(@"\[[a-z]{3,22}\]")]
    private static partial Regex TikTokCode();

    [GeneratedRegex(":[a-z0-9]+(?:-[a-z0-9]+)+:")]
    private static partial Regex YouTubeCode();

    public static IReadOnlyList<MessagePart> Build(
        ChatMessage message, IReadOnlyDictionary<string, string> channelEmotes, bool images)
    {
        var text = message.Platform switch
        {
            Platform.TikTok => TikTokCode().Replace(message.Text, m => Shortcodes.TikTok.GetValueOrDefault(m.Value, m.Value)),
            Platform.YouTube => YouTubeCode().Replace(message.Text, m => Shortcodes.YouTube.GetValueOrDefault(m.Value, m.Value)),
            _ => message.Text,
        };

        var parts = new List<MessagePart>();
        var plain = new StringBuilder();
        var count = 0;

        void Flush()
        {
            if (plain.Length > 0) parts.Add(new MessagePart(plain.ToString()));
            plain.Clear();
        }

        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0) plain.Append(' ');
            var word = words[i];
            if (word.Length == 0) continue;

            string? url = null;
            if (images && !message.Emotes.TryGetValue(word, out url) && message.Platform == Platform.Twitch)
                channelEmotes.TryGetValue(word, out url);

            foreach (var part in url is null ? Emoji.Split(word) : [new MessagePart(word, url)])
            {
                if (part.Image.Length == 0 || count >= MaxImages)
                {
                    plain.Append(part.Text);
                    continue;
                }

                Flush();
                parts.Add(part);
                count++;
            }
        }

        Flush();
        return parts;
    }
}
