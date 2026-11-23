using StreamChat.Core;
using StreamChat.Core.Chat;

namespace StreamChat.Tests.Chat;

public class ChatStateTests
{
    private static readonly TimeSpan Now = TimeSpan.Zero;

    private static ChatMessage Message(Platform platform, string id, string author = "user", string authorId = "u") =>
        new(platform, author, "text") { Id = id, AuthorId = authorId };

    private static OverlayState With(params ChatMessage[] messages)
    {
        var state = new OverlayState();
        foreach (var message in messages) state.Apply(new Posted(message), Now);
        return state;
    }

    [Fact]
    public void Replayed_history_is_not_shown_twice() =>
        Assert.Single(With(Message(Platform.YouTube, "1"), Message(Platform.YouTube, "1")).Messages);

    [Fact]
    public void Replay_is_recognised_after_it_scrolled_away()
    {
        var state = With(Enumerable.Range(0, 10).Select(i => Message(Platform.YouTube, $"{i}")).ToArray());
        state.Apply(new Posted(Message(Platform.YouTube, "0")), Now);
        Assert.DoesNotContain(state.Messages, m => m.Id == "0");
    }

    [Fact]
    public void Replay_does_not_wake_a_hidden_chat()
    {
        var state = With(Message(Platform.YouTube, "1"));
        state.Apply(new Posted(Message(Platform.YouTube, "1")), TimeSpan.FromSeconds(100));
        Assert.False(state.ShouldShow(TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void Same_id_on_two_platforms_is_two_messages() =>
        Assert.Equal(2, With(Message(Platform.Twitch, "1"), Message(Platform.YouTube, "1")).Messages.Count);

    [Fact]
    public void Moderator_deletion_removes_message()
    {
        var state = With(Message(Platform.Twitch, "1"), Message(Platform.Twitch, "2"));
        state.Apply(new Deleted(Platform.Twitch, "1"), Now);
        Assert.Equal("2", Assert.Single(state.Messages).Id);
    }

    [Fact]
    public void Deleting_the_last_message_hides_the_window()
    {
        var state = With(Message(Platform.Twitch, "1"));
        state.Apply(new Deleted(Platform.Twitch, "1"), Now);
        Assert.False(state.ShouldShow(Now));
    }

    [Fact]
    public void Ban_removes_all_messages_of_that_user_on_that_platform()
    {
        var state = With(Message(Platform.Twitch, "1"), Message(Platform.Twitch, "2"),
            Message(Platform.Twitch, "3", authorId: "other"), Message(Platform.YouTube, "4"));
        state.Apply(new UserCleared(Platform.Twitch, "u"), Now);
        Assert.Equal(["3", "4"], state.Messages.Select(m => m.Id));
    }

    [Fact]
    public void Hide_key_on_an_empty_chat_does_not_silently_disable_it()
    {
        var state = new OverlayState();
        state.ToggleHidden(Now);
        state.Apply(new Posted(Message(Platform.Twitch, "1")), Now);
        Assert.True(state.ShouldShow(Now));
    }

    [Fact]
    public void Clear_keeps_the_other_platform()
    {
        var state = With(Message(Platform.Twitch, "1"), Message(Platform.YouTube, "2"));
        state.Apply(new Cleared(Platform.Twitch), Now);
        Assert.Equal("2", Assert.Single(state.Messages).Id);
    }

    [Theory]
    [InlineData("Nightbot")]
    [InlineData("@nightbot")]
    [InlineData("other, NIGHTBOT")]
    [InlineData("other\r\n nightbot ")]
    public void Ignored_names_are_not_shown(string ignored)
    {
        var state = new OverlayState();
        state.SetIgnored(ignored);
        state.Apply(new Posted(Message(Platform.YouTube, "1", author: "@Nightbot")), Now);
        Assert.Empty(state.Messages);
    }

    [Fact]
    public void Ignore_matches_login_when_display_name_differs()
    {
        var state = new OverlayState();
        state.SetIgnored("botname");
        state.Apply(new Posted(Message(Platform.Twitch, "1", author: "Бот") with { Login = "botname" }), Now);
        Assert.Empty(state.Messages);
    }

    // имя донатера любое: вписал Nightbot и пропал бы вместе с деньгами
    [Fact]
    public void Donation_is_not_lost_to_the_ignore_list()
    {
        var state = new OverlayState();
        state.SetIgnored("nightbot");
        state.Apply(new Posted(Message(Platform.DonationAlerts, "1", author: "Nightbot") with { Mark = MessageMark.Donation }), Now);
        Assert.Single(state.Messages);
    }

    [Fact]
    public void Ignoring_removes_messages_already_shown()
    {
        var state = With(Message(Platform.Twitch, "1", author: "Bot"));
        state.SetIgnored("bot");
        Assert.Empty(state.Messages);
    }
}
