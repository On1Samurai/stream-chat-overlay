using StreamChat.Core;
using StreamChat.Core.Chat;
using StreamChat.Core.Viewers;

namespace StreamChat.Tests.Viewers;

public class ViewerTests
{
    private static TimeSpan At(double minutes) => TimeSpan.FromMinutes(minutes);

    private static ViewerReading Live(long count) => new(ViewerState.Live, count);

    [Theory]
    [InlineData("15060", 15060)]
    [InlineData("0\n", 0)]
    public void Twitch_number_is_live(string body, long count) =>
        Assert.Equal(Live(count), TwitchViewers.Parse("name", body));

    [Fact]
    public void Twitch_offline() =>
        Assert.Equal(ViewerState.Offline, TwitchViewers.Parse("gaules", "gaules is offline").State);

    [Theory]
    [InlineData("")]
    [InlineData("-5")]
    [InlineData("1,5")]
    [InlineData("<html>error</html>")]
    [InlineData("[Error from Twitch API] 500")]
    [InlineData("someone_else is offline")]
    [InlineData("99999999999999999999999")]
    public void Twitch_garbage_is_unknown(string body) =>
        Assert.Equal(ViewerState.Unknown, TwitchViewers.Parse("gaules", body).State);

    [Theory]
    [InlineData("""{"actualStartTime":"2026","concurrentViewers":"1234"}""", ViewerState.Live, 1234)]
    [InlineData("""{"actualStartTime":"2026","concurrentViewers":7}""", ViewerState.Live, 7)]
    [InlineData("""{"actualStartTime":"2026","actualEndTime":"2026"}""", ViewerState.Offline, 0)]
    [InlineData("""{"scheduledStartTime":"2026"}""", ViewerState.Offline, 0)]
    [InlineData("""{"actualStartTime":"2026"}""", ViewerState.Unknown, 0)]
    public void YouTube_details(string details, ViewerState state, long count) =>
        Assert.Equal(new ViewerReading(state, count),
            YouTubeApi.ParseViewers(200, """{"items":[{"liveStreamingDetails":""" + details + "}]}"));

    [Theory]
    [InlineData(403, "{}")]
    [InlineData(200, "<html>")]
    [InlineData(200, """{"items":[]}""")]
    [InlineData(200, "[]")]
    [InlineData(200, """{"items":[7]}""")]
    public void YouTube_garbage_is_unknown(int status, string body) =>
        Assert.Equal(ViewerState.Unknown, YouTubeApi.ParseViewers(status, body).State);

    [Theory]
    [InlineData("""{"event":"roomUser","data":{"viewerCount":321,"topViewers":[]}}""", 321L)]
    [InlineData("""{"event":"roomUser","data":{"viewerCount":"42"}}""", 42L)]
    [InlineData("""{"event":"roomUser","data":{"viewerCount":-1}}""", null)]
    [InlineData("""{"event":"chat","data":{"viewerCount":9}}""", null)]
    [InlineData("""{"event":"roomUser","data":7}""", null)]
    [InlineData("junk", null)]
    public void TikTok_room_event(string json, long? count) => Assert.Equal(count, TikFinityParser.ParseViewers(json));

    [Fact]
    public void Board_shows_each_platform_and_total()
    {
        var board = new ViewerBoard();
        board.Set(Platform.Twitch, Live(15060), At(0));
        board.Set(Platform.YouTube, Live(40), At(0));
        Assert.Equal("15 060", board.Text(Platform.Twitch, At(1)));
        Assert.Equal("15 100", board.Total([Platform.Twitch, Platform.YouTube], At(1)));
    }

    [Fact]
    public void Offline_and_unknown_are_different()
    {
        var board = new ViewerBoard();
        board.Set(Platform.Twitch, new ViewerReading(ViewerState.Offline), At(0));
        Assert.Equal("офлайн", board.Text(Platform.Twitch, At(0)));
        Assert.Equal("—", board.Text(Platform.YouTube, At(0)));
    }

    [Fact]
    public void Failed_request_keeps_the_last_number_for_three_minutes()
    {
        var board = new ViewerBoard();
        board.Set(Platform.Twitch, Live(10), At(0));
        board.Set(Platform.Twitch, new ViewerReading(ViewerState.Unknown), At(1));
        Assert.Equal("10", board.Text(Platform.Twitch, At(2.9)));
        Assert.Equal("—", board.Text(Platform.Twitch, At(3)));
    }

    [Fact]
    public void Forget_removes_the_number_at_once()
    {
        var board = new ViewerBoard();
        board.Set(Platform.Twitch, Live(10), At(0));
        board.Forget(Platform.Twitch);
        Assert.Equal("—", board.Text(Platform.Twitch, At(0)));
    }

    [Fact]
    public void Total_counts_only_shown_platforms()
    {
        var board = new ViewerBoard();
        board.Set(Platform.Twitch, Live(10), At(0));
        board.Set(Platform.TikTok, Live(90), At(0));
        Assert.Equal("10", board.Total([Platform.Twitch], At(0)));
        Assert.Equal("—", board.Total([Platform.YouTube], At(0)));
    }

    [Fact]
    public void Counters_stay_when_chat_is_silent()
    {
        var state = new OverlayState { CountersEnabled = true };
        Assert.False(state.ShouldShow(At(1)));
        Assert.True(state.ShouldShowCounters);
    }

    [Fact]
    public void Hide_key_hides_counters_at_the_first_press_in_silence()
    {
        var state = new OverlayState { CountersEnabled = true };
        state.ToggleHidden(At(1));
        Assert.False(state.ShouldShowCounters);
        state.ToggleHidden(At(2));
        Assert.True(state.ShouldShowCounters);
    }

    [Fact]
    public void Protection_failure_hides_counters()
    {
        var state = new OverlayState { CountersEnabled = true };
        state.ToggleEditing(At(0));
        state.Block();
        Assert.False(state.ShouldShowCounters);
    }

    [Fact]
    public void Counters_are_hidden_until_enabled() => Assert.False(new OverlayState().ShouldShowCounters);
}
