using StreamChat.Core.Chat;

namespace StreamChat.Tests.Chat;

public class TextTests
{
    [Theory]
    [InlineData("  привет  ", "привет")]
    [InlineData("a\nb\r\nc\td", "a b  c d")]
    [InlineData("<b>жирно</b>", "<b>жирно</b>")]
    [InlineData("abc‮cba", "abccba")]
    [InlineData("a⁦b⁩", "ab")]
    [InlineData("\0x\u0007", "x")]
    [InlineData(null, "")]
    [InlineData("\u200B\u200B\u200D", "")]
    [InlineData("\u3164\u2800\uFE0F", "")]
    [InlineData("\u0301\u0301", "")]
    [InlineData("a\u200Bb", "a\u200Bb")]
    public void Viewer_text_becomes_plain_one_line(string? input, string expected) =>
        Assert.Equal(expected, UntrustedText.Clean(input, 100));

    [Fact]
    public void Long_text_is_cut_without_breaking_an_emoji()
    {
        var clean = UntrustedText.Clean("ab😀cd", 3);
        Assert.Equal("ab…", clean);
    }

    [Fact]
    public void Emoji_sequence_survives() => Assert.Equal("👨‍👩‍👧", UntrustedText.Clean("👨‍👩‍👧", 100));
}
