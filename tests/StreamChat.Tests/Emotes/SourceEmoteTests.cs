using StreamChat.Core;
using StreamChat.Core.Chat;

namespace StreamChat.Tests.Emotes;

public class SourceEmoteTests
{
    private static ChatMessage Twitch(string line) => Assert.IsType<Posted>(TwitchParser.Parse(line).Event).Message;

    private static string Url(string id) => $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/static/dark/2.0";

    [Fact]
    public void Twitch_emotes_are_read_from_positions()
    {
        var message = Twitch("@emotes=25:0-4,12-16/1902:6-10;id=1 :v!v@v PRIVMSG #c :Kappa Keepo Kappa");
        Assert.Equal(new Dictionary<string, string> { ["Kappa"] = Url("25"), ["Keepo"] = Url("1902") }, message.Emotes);
    }

    [Fact]
    public void Twitch_positions_count_code_points_not_utf16() =>
        Assert.Equal(["Kappa"], Twitch("@emotes=25:3-7;id=1 :v!v@v PRIVMSG #c :😀😀 Kappa").Emotes.Keys);

    [Fact]
    public void Action_positions_are_relative_to_inner_text() =>
        Assert.Equal(["Kappa"], Twitch("@emotes=25:0-4;id=1 :v!v@v PRIVMSG #c :\u0001ACTION Kappa hi\u0001").Emotes.Keys);

    [Fact]
    public void Subscription_comment_has_emotes() =>
        Assert.Equal(["Kappa"], Twitch(@"@emotes=25:0-4;display-name=A;id=1;system-msg=Sub :t USERNOTICE #c :Kappa").Emotes.Keys);

    [Theory]
    [InlineData("25:0-99")]
    [InlineData("25:3-1")]
    [InlineData("25:-1-3")]
    [InlineData("25")]
    [InlineData("25:a-b")]
    [InlineData("..:0-4")]
    [InlineData("25:99999999999-99999999999")]
    public void Broken_emote_tag_is_ignored(string tag) =>
        Assert.Empty(Twitch($"@emotes={tag};id=1 :v!v@v PRIVMSG #c :Kappa").Emotes);

    [Fact]
    public void Room_id_is_read() => Assert.Equal("71092938", TwitchParser.Parse("@room-id=71092938 :t ROOMSTATE #c").RoomId);

    [Theory]
    [InlineData("""{"emoteId":"1","emoteImageUrl":"https://p16-sign-va.tiktokcdn.com/a.png"}""")]
    [InlineData("""{"emote":{"emoteId":"1","image":{"imageUrl":"https://p16-sign-va.tiktokcdn.com/a.png"}}}""")]
    [InlineData("""{"emote":{"image":{"urlList":["https://p16-sign-va.tiktokcdn.com/a.png"]}}}""")]
    public void TikTok_sticker_without_text_is_shown(string emote)
    {
        var json = """{"event":"chat","data":{"comment":"","uniqueId":"v","emotes":[EMOTE]}}""".Replace("EMOTE", emote);
        var message = Assert.IsType<Posted>(TikFinityParser.Parse(json)).Message;
        Assert.Equal("[стикер-1]", message.Text);
        Assert.Equal("https://p16-sign-va.tiktokcdn.com/a.png", message.Emotes["[стикер-1]"]);
    }

    [Fact]
    public void TikTok_sticker_from_a_foreign_host_is_not_loaded() =>
        Assert.Null(TikFinityParser.Parse("""{"event":"chat","data":{"comment":"","uniqueId":"v","emotes":[{"emoteImageUrl":"https://evil.example/ip.png"}]}}"""));
}
