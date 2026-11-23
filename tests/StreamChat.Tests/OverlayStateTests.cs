using StreamChat.Core;

namespace StreamChat.Tests;

public class OverlayStateTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    private static OverlayState WithMessage()
    {
        var state = new OverlayState();
        state.Add(new ChatMessage(Platform.Twitch, "зритель", "привет"), At(0));
        return state;
    }

    [Fact]
    public void Empty_chat_is_hidden() => Assert.False(new OverlayState().ShouldShow(At(0)));

    [Theory]
    [InlineData(0, true)]
    [InlineData(14.9, true)]
    [InlineData(15, false)]
    public void Chat_hides_after_15_seconds_of_silence(double seconds, bool visible) =>
        Assert.Equal(visible, WithMessage().ShouldShow(At(seconds)));

    [Fact]
    public void New_message_brings_chat_back()
    {
        var state = WithMessage();
        state.Add(new ChatMessage(Platform.YouTube, "другой", "снова"), At(40));
        Assert.True(state.ShouldShow(At(41)));
    }

    [Fact]
    public void Only_six_latest_messages_are_kept()
    {
        var state = new OverlayState();
        for (var i = 0; i < 8; i++) state.Add(new ChatMessage(Platform.Twitch, "a", $"{i}"), At(i));
        Assert.Equal(["2", "3", "4", "5", "6", "7"], state.Messages.Select(m => m.Text));
    }

    [Fact]
    public void Messages_do_not_reveal_manually_hidden_chat()
    {
        var state = WithMessage();
        state.ToggleHidden(At(1));
        state.Add(new ChatMessage(Platform.Twitch, "a", "b"), At(2));
        Assert.False(state.ShouldShow(At(2)));
    }

    [Fact]
    public void Showing_manually_restarts_the_silence_timer()
    {
        var state = WithMessage();
        state.ToggleHidden(At(1));
        state.ToggleHidden(At(60));
        Assert.True(state.ShouldShow(At(74)));
        Assert.False(state.ShouldShow(At(75)));
    }

    [Fact]
    public void Edit_mode_is_visible_without_messages_even_if_hidden()
    {
        var state = new OverlayState();
        state.ToggleHidden(At(0));
        state.ToggleEditing(At(1));
        Assert.True(state.ShouldShow(At(500)));
    }

    [Fact]
    public void Hiding_leaves_edit_mode()
    {
        var state = WithMessage();
        state.ToggleEditing(At(1));
        state.ToggleHidden(At(2));
        Assert.False(state.Editing);
        Assert.False(state.ShouldShow(At(2)));
    }

    [Fact]
    public void Protection_failure_hides_everything()
    {
        var state = WithMessage();
        state.ToggleEditing(At(1));
        state.Block();
        state.Add(new ChatMessage(Platform.Twitch, "a", "b"), At(2));
        Assert.False(state.Editing);
        Assert.False(state.ShouldShow(At(2)));
    }

    [Fact]
    public void Hotkey_after_failure_requests_another_protected_show()
    {
        var state = WithMessage();
        state.Block();
        state.ToggleHidden(At(30));
        Assert.True(state.ShouldShow(At(30)));
    }

    [Fact]
    public void Edit_hotkey_after_failure_requests_another_protected_show()
    {
        var state = new OverlayState();
        state.Block();
        state.ToggleEditing(At(1));
        Assert.True(state.ShouldShow(At(1)));
    }

    [Fact]
    public void Second_launch_reveals_hidden_chat()
    {
        var state = WithMessage();
        state.ToggleHidden(At(1));
        state.Reveal(At(50));
        Assert.True(state.ShouldShow(At(50)));
    }

    [Fact]
    public void Hotkey_during_auto_hide_shows_chat_instead_of_disabling_it()
    {
        var state = WithMessage();
        state.ToggleHidden(At(20));
        Assert.True(state.ShouldShow(At(20)));
    }

    [Fact]
    public void Only_the_first_failure_is_reported()
    {
        var state = WithMessage();
        Assert.True(state.Block());
        Assert.False(state.Block());
    }

    [Fact]
    public void Failure_after_retry_is_reported_again()
    {
        var state = WithMessage();
        state.Block();
        state.ToggleHidden(At(1));
        Assert.True(state.Block());
    }

    [Fact]
    public void Lower_message_limit_drops_the_oldest()
    {
        var state = new OverlayState();
        for (var i = 0; i < 5; i++) state.Add(new ChatMessage(Platform.Twitch, "a", $"{i}"), At(i));
        state.Configure(maxMessages: 2, hideAfterSeconds: 15);
        Assert.Equal(["3", "4"], state.Messages.Select(m => m.Text));
    }

    [Fact]
    public void Silence_time_is_configurable()
    {
        var state = WithMessage();
        state.Configure(6, 40);
        Assert.True(state.ShouldShow(At(39)));
        Assert.False(state.ShouldShow(At(40)));
    }

    [Fact]
    public void Zero_silence_time_never_hides()
    {
        var state = WithMessage();
        state.Configure(6, 0);
        Assert.True(state.ShouldShow(At(100_000)));
    }

    [Fact]
    public void Preview_is_visible_while_settings_are_open()
    {
        var state = WithMessage();
        state.Previewing = true;
        Assert.True(state.ShouldShow(At(500)));
    }

    [Fact]
    public void Hide_key_works_while_settings_are_open()
    {
        var state = WithMessage();
        state.Previewing = true;
        state.ToggleHidden(At(1));
        Assert.False(state.ShouldShow(At(2)));
    }

    [Fact]
    public void Preview_never_overrides_protection_failure()
    {
        var state = WithMessage();
        state.Previewing = true;
        state.Block();
        Assert.False(state.ShouldShow(At(1)));
    }
}
