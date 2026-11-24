using StreamChat.Core.Updates;

namespace StreamChat.Tests.Rules;

public class UpdateTests
{
    private static UpdateResult Check(string current, string tag, int status = 200) =>
        UpdateChecker.Parse(current, status, """{"tag_name":"TAG","html_url":"https://evil.example/x"}""".Replace("TAG", tag));

    [Theory]
    [InlineData("1.0.0", "v1.0.1")]
    [InlineData("1.9.0", "v1.10.0")]
    [InlineData("1.1.0-rc5", "v1.1.0")]
    [InlineData("1.1.0-rc5", "1.1.0-rc10")]
    public void Newer_release_is_offered(string current, string tag) =>
        Assert.Equal(new UpdateResult(UpdateState.Available, tag.TrimStart('v')), Check(current, tag));

    [Theory]
    [InlineData("1.0.0", "v1.0.0")]
    [InlineData("1.1.0", "v1.0.9")]
    [InlineData("1.1.0-rc5", "v1.0.0")]
    [InlineData("1.1.0", "v1.1.0-rc9")]
    public void Same_or_older_is_up_to_date(string current, string tag) =>
        Assert.Equal(UpdateState.UpToDate, Check(current, tag).State);

    [Fact]
    public void No_releases_yet() => Assert.Equal(UpdateState.NoReleases, Check("1.0.0", "v9.0.0", 404).State);

    [Theory]
    [InlineData("latest", 200)]
    [InlineData("v1.2", 200)]
    [InlineData("v9.0.0\\nзлой текст", 200)]
    [InlineData("v9.0.0", 403)]
    [InlineData("v9.0.0", 500)]
    public void Anything_else_is_a_failure(string tag, int status) => Assert.Equal(UpdateState.Failed, Check("1.0.0", tag, status).State);

    [Fact]
    public void Not_json_is_a_failure() => Assert.Equal(UpdateState.Failed, UpdateChecker.Parse("1.0.0", 200, "<html>").State);

    [Fact]
    public void Default_repository_is_the_release_one() => Assert.Equal("On1Samurai/stream-chat-overlay", UpdateRepository.Name);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("evil")]
    [InlineData("a/b/c")]
    [InlineData("a/b c")]
    [InlineData("a/b\n")]
    [InlineData("a@b/c")]
    public void Bad_repository_falls_back(string? value) => Assert.Equal(UpdateRepository.Default, UpdateRepository.Pick(value));

    [Fact]
    public void Good_repository_is_kept() => Assert.Equal("On1Samurai/stream-chat-overlay-update-test", UpdateRepository.Pick("On1Samurai/stream-chat-overlay-update-test"));
}
