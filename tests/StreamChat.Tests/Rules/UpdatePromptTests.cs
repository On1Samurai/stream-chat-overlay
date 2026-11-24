using StreamChat.Core.Updates;

namespace StreamChat.Tests.Rules;

public class UpdatePromptTests
{
    private static UpdatePrompt Prompt(string version, UpdateCheckSource source)
    {
        var prompt = new UpdatePrompt();
        prompt.Found(version, source);
        return prompt;
    }

    [Fact]
    public void Nothing_found_asks_nothing() => Assert.Null(new UpdatePrompt().ShouldAsk(true, false, null));

    [Fact]
    public void Checks_off_never_asks() => Assert.Null(Prompt("1.3.0", UpdateCheckSource.Manual).ShouldAsk(false, false, null));

    [Fact]
    public void Obs_on_waits_for_idle()
    {
        var prompt = Prompt("1.3.0", UpdateCheckSource.Daily);
        Assert.Null(prompt.ShouldAsk(true, true, null));
        Assert.Null(prompt.ShouldAsk(true, true, false));
        Assert.Equal("1.3.0", prompt.ShouldAsk(true, true, true));
    }

    [Fact]
    public void Obs_off_asks_only_first_after_start()
    {
        Assert.Equal("1.3.0", Prompt("1.3.0", UpdateCheckSource.FirstAfterStart).ShouldAsk(true, false, null));
        Assert.Null(Prompt("1.3.0", UpdateCheckSource.Daily).ShouldAsk(true, false, null));
    }

    [Fact]
    public void Manual_asks_even_without_obs()
    {
        Assert.Equal("1.3.0", Prompt("1.3.0", UpdateCheckSource.Manual).ShouldAsk(true, false, null));
        Assert.Null(Prompt("1.3.0", UpdateCheckSource.Manual).ShouldAsk(true, true, false));
    }

    [Fact]
    public void Manual_with_obs_unknown_asks()
    {
        Assert.Equal("1.3.0", Prompt("1.3.0", UpdateCheckSource.Manual).ShouldAsk(true, true, null));
        Assert.Null(Prompt("1.3.0", UpdateCheckSource.FirstAfterStart).ShouldAsk(true, true, null));
    }

    [Fact]
    public void Manual_allowance_ends_when_hidden_on_air()
    {
        var prompt = Prompt("1.3.0", UpdateCheckSource.Manual);
        Assert.Equal("1.3.0", prompt.ShouldAsk(true, true, null));
        prompt.Asked();
        prompt.Hidden();
        Assert.Null(prompt.ShouldAsk(true, true, null));
        Assert.Equal("1.3.0", prompt.ShouldAsk(true, true, true));
    }

    [Fact]
    public void Manual_refused_on_air_does_not_ask_on_unknown()
    {
        var prompt = Prompt("1.3.0", UpdateCheckSource.Manual);
        Assert.Null(prompt.ShouldAsk(true, true, false));
        Assert.Null(prompt.ShouldAsk(true, true, null));
        Assert.Equal("1.3.0", prompt.ShouldAsk(true, true, true));
    }

    [Fact]
    public void Postpone_silences_until_manual()
    {
        var prompt = Prompt("1.3.0", UpdateCheckSource.FirstAfterStart);
        prompt.Asked();
        prompt.Postpone();
        Assert.Null(prompt.ShouldAsk(true, false, null));
        prompt.Found("1.3.0", UpdateCheckSource.Daily);
        Assert.Null(prompt.ShouldAsk(true, false, null));
        prompt.Found("1.3.0", UpdateCheckSource.Manual);
        Assert.Equal("1.3.0", prompt.ShouldAsk(true, false, null));
    }

    [Fact]
    public void Newer_version_after_postpone_is_asked()
    {
        var prompt = Prompt("1.3.0", UpdateCheckSource.FirstAfterStart);
        prompt.Asked();
        prompt.Postpone();
        prompt.Found("1.4.0", UpdateCheckSource.FirstAfterStart);
        Assert.Equal("1.4.0", prompt.ShouldAsk(true, false, null));
    }

    [Fact]
    public void Asked_is_not_repeated()
    {
        var prompt = Prompt("1.3.0", UpdateCheckSource.FirstAfterStart);
        prompt.Asked();
        Assert.Null(prompt.ShouldAsk(true, false, null));
        prompt.Closed();
        Assert.Null(prompt.ShouldAsk(true, false, null));
    }

    [Fact]
    public void Hidden_asks_again_when_idle()
    {
        var prompt = Prompt("1.3.0", UpdateCheckSource.Daily);
        prompt.Asked();
        prompt.Hidden();
        Assert.Null(prompt.ShouldAsk(true, true, false));
        Assert.Equal("1.3.0", prompt.ShouldAsk(true, true, true));
    }

    [Fact]
    public void Daily_after_first_keeps_the_stronger_source()
    {
        var prompt = Prompt("1.3.0", UpdateCheckSource.FirstAfterStart);
        prompt.Found("1.3.0", UpdateCheckSource.Daily);
        Assert.Equal("1.3.0", prompt.ShouldAsk(true, false, null));
    }

    [Fact]
    public void Repeated_postpone_keeps_the_postponed_version()
    {
        var prompt = Prompt("1.3.0", UpdateCheckSource.FirstAfterStart);
        prompt.Asked();
        prompt.Postpone();
        prompt.Closed();
        prompt.Found("1.3.0", UpdateCheckSource.Daily);
        Assert.Null(prompt.ShouldAsk(true, false, null));
    }

    [Fact]
    public void Newer_version_found_while_window_open_is_asked_after_close()
    {
        var prompt = Prompt("1.3.0", UpdateCheckSource.Daily);
        prompt.Asked();
        prompt.Found("1.4.0", UpdateCheckSource.Daily);
        prompt.Closed();
        Assert.Equal("1.4.0", prompt.ShouldAsk(true, true, true));
        prompt.Found("1.3.0", UpdateCheckSource.Daily);
        Assert.Equal("1.4.0", prompt.ShouldAsk(true, true, true));
    }

    [Fact]
    public void Manual_with_obs_idle_asks() => Assert.Equal("1.3.0", Prompt("1.3.0", UpdateCheckSource.Manual).ShouldAsk(true, true, true));

    [Fact]
    public void Double_postpone_does_not_silence_unseen_newer_version()
    {
        var prompt = Prompt("1.3.0", UpdateCheckSource.Daily);
        prompt.Asked();
        prompt.Found("1.4.0", UpdateCheckSource.Daily);
        prompt.Postpone();
        prompt.Closed();
        Assert.Equal("1.4.0", prompt.ShouldAsk(true, true, true));
        prompt.Found("1.3.0", UpdateCheckSource.Daily);
        Assert.Equal("1.4.0", prompt.ShouldAsk(true, true, true));
    }
}
