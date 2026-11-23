using StreamChat.Core.Emotes;

namespace StreamChat.Tests.Emotes;

public class EmojiTests
{
    private static string Images(string text) =>
        string.Join(",", Emoji.Split(text).Where(part => part.Image.Length > 0).Select(part => part.Image["emoji:".Length..]));

    [Theory]
    [InlineData("😀", "1f600")]
    [InlineData("👍🏽", "1f44d-1f3fd")]
    [InlineData("👨‍👩‍👧", "1f468-200d-1f469-200d-1f467")]
    [InlineData("🇷🇺🇧🇾", "1f1f7-1f1fa,1f1e7-1f1fe")]
    [InlineData("❤️", "2764")]
    [InlineData("❤", "2764")]
    [InlineData("1️⃣", "31-20e3")]
    [InlineData("🏳️‍🌈", "1f3f3-fe0f-200d-1f308")]
    [InlineData("😂😂", "1f602,1f602")]
    [InlineData("©️", "a9")]
    [InlineData("⏰", "23f0")]
    public void Emoji_becomes_a_picture(string text, string images) => Assert.Equal(images, Images(text));

    [Theory]
    [InlineData("обычный текст 123 #*")]
    [InlineData("© ® ™ ↔ ‼")]
    [InlineData("❤\uFE0E")]
    [InlineData("")]
    public void Ordinary_text_stays_text(string text) => Assert.Equal("", Images(text));

    [Fact]
    public void Text_around_emoji_is_kept()
    {
        var parts = Emoji.Split("привет 👋 всем");
        Assert.Equal(["привет ", "👋", " всем"], parts.Select(part => part.Text));
    }

    [Fact]
    public void Every_reference_has_a_png_inside_the_program()
    {
        var bytes = Emoji.Png("1f600");
        Assert.NotNull(bytes);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes[..4]);
        Assert.Null(Emoji.Png("../secret"));
    }
}
