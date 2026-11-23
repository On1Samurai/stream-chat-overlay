using StreamChat.Core.Chat;

namespace StreamChat.Tests.Chat;

public class LinkTests
{
    [Theory]
    [InlineData("SomeName", "somename")]
    [InlineData(" @some_name ", "some_name")]
    [InlineData("#name123", "name123")]
    [InlineData("https://www.twitch.tv/SomeName", "somename")]
    [InlineData("twitch.tv/somename/", "somename")]
    [InlineData("https://m.twitch.tv/somename?x=1", "somename")]
    [InlineData("https://www.twitch.tv/popout/somename/chat", "somename")]
    [InlineData("https://www.twitch.tv/embed/somename/chat?parent=x", "somename")]
    [InlineData("https://www.twitch.tv/popout/moderator/somename/chat", "somename")]
    [InlineData("https://www.twitch.tv/popout/u/somename/stream-manager/chat", "somename")]
    [InlineData("ab", null)]
    [InlineData("имя", null)]
    [InlineData("name\r\nJOIN #other", null)]
    [InlineData("https://evil.example/somename", null)]
    [InlineData("", null)]
    public void Twitch_channel(string input, string? expected) => Assert.Equal(expected, ChatLinks.TwitchChannel(input));

    [Theory]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1", "dQw4w9WgXcQ")]
    [InlineData("youtube.com/watch?feature=x&v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/live/dQw4w9WgXcQ?feature=share", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live_chat?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://studio.youtube.com/video/dQw4w9WgXcQ/livestreaming", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/@channel/live", null)]
    [InlineData("https://evil.example/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://www.youtube.com/watch?v=short", null)]
    [InlineData("", null)]
    public void YouTube_video(string input, string? expected) => Assert.Equal(expected, ChatLinks.YouTubeVideo(input));
}
