using StreamChat.Core.Emotes;

namespace StreamChat.Tests.Emotes;

public class EmoteSourcesTests
{
    [Fact]
    public void SevenTv_set_uses_still_png()
    {
        var emotes = ThirdPartyEmotes.ParseSevenTv("""
            {"emote_set":{"emotes":[{"id":"AAA","name":"Moving","data":{"animated":true}},{"id":"BBB","name":"Still","data":{"animated":false}}]}}
            """);
        Assert.Equal("https://cdn.7tv.app/emote/AAA/2x_static.png", emotes["Moving"]);
        Assert.Equal("https://cdn.7tv.app/emote/BBB/2x.png", emotes["Still"]);
    }

    [Fact]
    public void SevenTv_global_set_has_no_wrapper() =>
        Assert.Single(ThirdPartyEmotes.ParseSevenTv("""{"emotes":[{"id":"A","name":"x","data":{"animated":false}}]}"""));

    [Fact]
    public void Bttv_global_list() =>
        Assert.Equal("https://cdn.betterttv.net/emote/abc/2x.png", ThirdPartyEmotes.ParseBttv("""[{"id":"abc","code":":tf:"}]""")[":tf:"]);

    [Fact]
    public void Bttv_channel_lists() =>
        Assert.Equal(2, ThirdPartyEmotes.ParseBttv("""{"channelEmotes":[{"id":"a","code":"A"}],"sharedEmotes":[{"id":"b","code":"B"}]}""").Count);

    [Fact]
    public void Ffz_uses_2x_or_falls_back_to_1x()
    {
        var emotes = ThirdPartyEmotes.ParseFfz("""
            {"default_sets":[3],"sets":{"3":{"emoticons":[
              {"name":"Two","urls":{"1":"https://cdn.frankerfacez.com/emote/1/1","2":"https://cdn.frankerfacez.com/emote/1/2"}},
              {"name":"One","urls":{"1":"https://cdn.frankerfacez.com/emote/2/1"}}]}}}
            """);
        Assert.EndsWith("/1/2", emotes["Two"]);
        Assert.EndsWith("/2/1", emotes["One"]);
    }

    [Fact]
    public void Ffz_global_ignores_sets_that_are_not_for_everyone() =>
        Assert.Empty(ThirdPartyEmotes.ParseFfz("""{"default_sets":[3],"sets":{"9":{"emoticons":[{"name":"x","urls":{"1":"https://cdn.frankerfacez.com/emote/2/1"}}]}}}"""));

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("<html>")]
    [InlineData("""{"emotes":{},"sets":[],"channelEmotes":7}""")]
    [InlineData("""{"emotes":[{"id":"../../x","name":"A"}],"sets":{"1":{"emoticons":[{"name":"B","urls":{"1":"https://evil.example/x"}}]}},"channelEmotes":[{"id":"a/b","code":"C"}]}""")]
    public void Broken_or_hostile_lists_give_nothing(string json)
    {
        Assert.Empty(ThirdPartyEmotes.ParseSevenTv(json));
        Assert.Empty(ThirdPartyEmotes.ParseBttv(json));
        Assert.Empty(ThirdPartyEmotes.ParseFfz(json));
    }

    [Theory]
    [InlineData("https://static-cdn.jtvnw.net/emoticons/v2/25/static/dark/2.0", true)]
    [InlineData("https://cdn.7tv.app/emote/A/2x.png", true)]
    [InlineData("https://p16-sign-va.tiktokcdn.com/a.png", true)]
    [InlineData("https://p16.tiktokcdn-us.com/a.png", true)]
    [InlineData("http://cdn.7tv.app/emote/A/2x.png", false)]
    [InlineData("https://cdn.7tv.app:8443/x", false)]
    [InlineData("https://user@cdn.7tv.app/x", false)]
    [InlineData("https://cdn.7tv.app.evil.example/x", false)]
    [InlineData("https://eviltiktokcdn.com/x", false)]
    [InlineData("https://127.0.0.1/x", false)]
    [InlineData("file:///c:/x.png", false)]
    public void Only_known_cdns_are_contacted(string url, bool allowed) => Assert.Equal(allowed, EmoteImages.IsAllowed(url));

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, true)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, true)]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67, 0x20, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { }, false)]
    public void Only_known_image_formats_are_decoded(byte[] bytes, bool known) => Assert.Equal(known, EmoteImages.IsKnownFormat(bytes));
}
