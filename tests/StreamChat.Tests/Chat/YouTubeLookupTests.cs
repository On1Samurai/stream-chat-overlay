using StreamChat.Core.Chat;

namespace StreamChat.Tests.Chat;

public class YouTubeLookupTests
{
    private static LookupProblem Problem(int status, string json) => YouTubeApi.ParseLookup(status, json).Problem;

    private static string Video(string details) => """{"items":[{"id":"x","liveStreamingDetails":""" + details + "}]}";

    private static string Error(string reason) =>
        """{"error":{"errors":[{"reason":"REASON"}],"details":[{"reason":"REASON"}]}}""".Replace("REASON", reason);

    [Fact]
    public void Live_stream_gives_chat_id() =>
        Assert.Equal(new LookupResult("abc", LookupProblem.None),
            YouTubeApi.ParseLookup(200, Video("""{"actualStartTime":"2026","activeLiveChatId":"abc"}""")));

    [Fact]
    public void Missing_video() => Assert.Equal(LookupProblem.NotFound, Problem(200, """{"items":[]}"""));

    [Fact]
    public void Ordinary_video() => Assert.Equal(LookupProblem.NotLiveVideo, Problem(200, """{"items":[{"id":"x"}]}"""));

    [Fact]
    public void Scheduled_stream() => Assert.Equal(LookupProblem.NotStarted, Problem(200, Video("""{"scheduledStartTime":"2026"}""")));

    [Fact]
    public void Finished_stream() =>
        Assert.Equal(LookupProblem.Ended, Problem(200, Video("""{"actualStartTime":"2026","actualEndTime":"2026"}""")));

    [Fact]
    public void Disabled_chat() => Assert.Equal(LookupProblem.ChatDisabled, Problem(200, Video("""{"actualStartTime":"2026"}""")));

    [Theory]
    [InlineData(400, "API_KEY_INVALID", LookupProblem.BadKey)]
    [InlineData(400, "keyInvalid", LookupProblem.BadKey)]
    [InlineData(403, "accessNotConfigured", LookupProblem.ApiDisabled)]
    [InlineData(403, "SERVICE_DISABLED", LookupProblem.ApiDisabled)]
    [InlineData(403, "quotaExceeded", LookupProblem.Quota)]
    [InlineData(429, "rateLimitExceeded", LookupProblem.Unavailable)]
    [InlineData(403, "API_KEY_HTTP_REFERRER_BLOCKED", LookupProblem.KeyRestricted)]
    [InlineData(403, "API_KEY_SERVICE_BLOCKED", LookupProblem.KeyRestricted)]
    [InlineData(500, "backendError", LookupProblem.Unavailable)]
    public void Api_errors(int status, string reason, LookupProblem expected) =>
        Assert.Equal(expected, Problem(status, Error(reason)));

    // так отвечает заглушка провайдера вместо YouTube
    [Theory]
    [InlineData(200, "<html>blocked</html>")]
    [InlineData(403, "<html>blocked</html>")]
    [InlineData(200, "[]")]
    public void Not_an_api_answer(int status, string body) => Assert.Equal(LookupProblem.Unavailable, Problem(status, body));
}
