using StreamChat.Core;
using StreamChat.Core.Emotes;

namespace StreamChat.Tests.Emotes;

public class MessageContentTests
{
    private const string Kappa = "https://static-cdn.jtvnw.net/emoticons/v2/25/static/dark/2.0";
    private static readonly Dictionary<string, string> None = [];
    private static readonly Dictionary<string, string> Channel = new() { ["KEKW"] = "https://cdn.7tv.app/emote/K/2x.png" };

    private static IReadOnlyList<MessagePart> Build(ChatMessage message, IReadOnlyDictionary<string, string>? channel = null, bool images = true) =>
        MessageContent.Build(message, channel ?? None, images);

    private static ChatMessage Twitch(string text) =>
        new(Platform.Twitch, "a", text) { Emotes = new Dictionary<string, string> { ["Kappa"] = Kappa } };

    private static string Images(IEnumerable<MessagePart> parts) =>
        string.Join(",", parts.Where(part => part.Image.Length > 0).Select(part => part.Image));

    [Fact]
    public void Platform_emote_replaces_its_word()
    {
        var parts = Build(Twitch("ну Kappa да"));
        Assert.Equal([new("ну "), new("Kappa", Kappa), new(" да")], parts);
    }

    [Fact]
    public void Emote_inside_another_word_stays_text() => Assert.Equal("", Images(Build(Twitch("KappaPride superKappa"))));

    [Fact]
    public void Channel_emote_is_shown() => Assert.Equal(Channel["KEKW"], Images(Build(Twitch("KEKW"), Channel)));

    [Fact]
    public void Channel_emotes_are_only_for_twitch() =>
        Assert.Equal("", Images(Build(new ChatMessage(Platform.YouTube, "a", "KEKW"), Channel)));

    [Fact]
    public void Switched_off_images_leave_words_but_keep_emoji() =>
        Assert.Equal("emoji:1f600", Images(Build(Twitch("Kappa KEKW 😀"), Channel, images: false)));

    [Fact]
    public void Platform_emote_wins_over_channel_emote() =>
        Assert.Equal(Kappa, Images(Build(Twitch("Kappa"), new Dictionary<string, string> { ["Kappa"] = "https://cdn.7tv.app/x" })));

    [Theory]
    [InlineData("[laughwithtears]", "emoji:1f602")]
    [InlineData("ой[cry][cry]", "emoji:1f622,emoji:1f622")]
    public void TikTok_codes_become_emoji(string text, string images) =>
        Assert.Equal(images, Images(Build(new ChatMessage(Platform.TikTok, "a", text))));

    [Theory]
    [InlineData(":face-blue-smiling:", "emoji:1f642")]
    [InlineData("gg:trophy-yellow-smiling::trophy-yellow-smiling:", "emoji:1f3c6,emoji:1f3c6")]
    public void YouTube_codes_become_emoji(string text, string images) =>
        Assert.Equal(images, Images(Build(new ChatMessage(Platform.YouTube, "a", text))));

    [Theory]
    [InlineData(Platform.TikTok, "[неизвестно] [notacode]")]
    [InlineData(Platform.YouTube, "12:30:45 :_memberWave:")]
    [InlineData(Platform.Twitch, "[cry] :face-blue-smiling:")]
    public void Unknown_or_foreign_codes_stay_text(Platform platform, string text) =>
        Assert.Equal(text, string.Concat(Build(new ChatMessage(platform, "a", text)).Select(part => part.Text)));

    [Fact]
    public void Every_known_code_has_a_picture_inside_the_program()
    {
        foreach (var code in Shortcodes.TikTok.Values.Concat(Shortcodes.YouTube.Values))
            Assert.All(Emoji.Split(code), part => Assert.NotEqual("", part.Image));
    }

    [Fact]
    public void Knows_all_46_tiktok_codes() => Assert.Equal(46, Shortcodes.TikTok.Count);

    [Fact]
    public void Picture_flood_is_limited()
    {
        var parts = Build(Twitch(string.Join(' ', Enumerable.Repeat("Kappa", 90))));
        Assert.Equal(MessageContent.MaxImages, parts.Count(part => part.Image.Length > 0));
    }
}
