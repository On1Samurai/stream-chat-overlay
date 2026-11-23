using StreamChat.Core;
using StreamChat.Core.Chat;
using StreamChat.Core.Rules;

namespace StreamChat.Tests.Rules;

// деньги не должны пропадать с экрана раньше, чем их заметят
public class MoneyTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    private static ChatMessage Chat(string id, MessageMark mark = MessageMark.None, string author = "viewer") =>
        new(Platform.Twitch, author, $"текст {id}") { Id = id, Mark = mark, Login = author };

    private static void Post(OverlayState state, ChatMessage message, double at) => state.Apply(new Posted(message), At(at));

    [Fact]
    public void Flood_does_not_push_out_a_fresh_donation()
    {
        var state = new OverlayState();
        Post(state, Chat("$", MessageMark.Donation), 0);
        for (var i = 0; i < 40; i++) Post(state, Chat($"{i}"), 0.1);
        Assert.Contains(state.Messages, m => m.Id == "$");
        Assert.Equal(6, state.Messages.Count);
    }

    [Theory]
    [InlineData(MessageMark.Paid)]
    [InlineData(MessageMark.Subscription)]
    public void Other_money_is_protected_too(MessageMark mark)
    {
        var state = new OverlayState();
        Post(state, Chat("$", mark), 0);
        for (var i = 0; i < 20; i++) Post(state, Chat($"{i}"), 1);
        Assert.Contains(state.Messages, m => m.Id == "$");
    }

    [Fact]
    public void Donation_gives_way_after_half_a_minute()
    {
        var state = new OverlayState();
        Post(state, Chat("$", MessageMark.Donation), 0);
        for (var i = 0; i < 6; i++) Post(state, Chat($"{i}"), 31);
        Assert.DoesNotContain(state.Messages, m => m.Id == "$");
    }

    [Fact]
    public void Hidden_chat_keeps_the_donation_until_shown()
    {
        var state = new OverlayState();
        Post(state, Chat("first"), 0);
        state.ToggleHidden(At(1));
        Post(state, Chat("$", MessageMark.Donation), 2);
        for (var i = 0; i < 20; i++) Post(state, Chat($"{i}"), 300);
        state.ToggleHidden(At(301));
        Assert.Contains(state.Messages, m => m.Id == "$");
    }

    [Fact]
    public void Limit_holds_even_when_everything_is_money()
    {
        var state = new OverlayState();
        for (var i = 0; i < 9; i++) Post(state, Chat($"{i}", MessageMark.Donation), 0);
        Assert.Equal(6, state.Messages.Count);
    }

    [Fact]
    public void Gift_wave_leaves_room_for_chat()
    {
        var state = new OverlayState();
        for (var i = 0; i < 9; i++) Post(state, Chat($"gift{i}", MessageMark.Paid), 0);
        Post(state, Chat("hello"), 1);
        Assert.Contains(state.Messages, m => m.Id == "hello");
        Assert.Contains(state.Messages, m => m.Id == "gift8");
        Assert.Equal(6, state.Messages.Count);
    }

    [Fact]
    public void Lower_limit_keeps_the_donation()
    {
        var state = new OverlayState();
        Post(state, Chat("$", MessageMark.Donation), 0);
        for (var i = 0; i < 5; i++) Post(state, Chat($"{i}"), 1);
        state.Configure(2, 15);
        Assert.Contains(state.Messages, m => m.Id == "$");
    }

    [Theory]
    [InlineData(MessageMark.Paid)]
    [InlineData(MessageMark.Subscription)]
    [InlineData(MessageMark.Donation)]
    public void Ignore_list_never_hides_money(MessageMark mark)
    {
        var state = new OverlayState();
        state.SetIgnored("nightbot");
        Post(state, Chat("$", mark, author: "Nightbot"), 0);
        state.SetIgnored("nightbot, other");
        Assert.Single(state.Messages);
    }

    [Fact]
    public void Gift_combo_update_replaces_the_line()
    {
        var state = new OverlayState();
        Post(state, Chat("g", MessageMark.Paid) with { Revision = "1", Text = "Rose ×1" }, 0);
        Post(state, Chat("other"), 1);
        Post(state, Chat("g", MessageMark.Paid) with { Revision = "3", Text = "Rose ×3" }, 2);
        Post(state, Chat("g", MessageMark.Paid) with { Revision = "3", Text = "Rose ×3" }, 3);
        Assert.Equal(["Rose ×3", "текст other"], state.Messages.Select(m => m.Text));
    }

    [Fact]
    public void Switched_off_highlight_keeps_the_marks_for_protection()
    {
        var rules = new ChatRules(new AppSettings { Highlight = false });
        Assert.Equal(MessageMark.Donation, rules.Check(Chat("$", MessageMark.Donation), At(0))!.Mark);
        Assert.Equal(MessageMark.Raid, rules.Check(Chat("r", MessageMark.Raid), At(0))!.Mark);
    }

    [Fact]
    public void Word_filter_hides_only_the_comment_of_a_subscription()
    {
        var rules = new ChatRules(new AppSettings { BlockedWords = "спойлер" });
        var sub = Chat("s", MessageMark.Subscription) with { Text = "Fan subscribed: спойлер", Amount = "Fan subscribed" };
        Assert.Equal("Fan subscribed [текст скрыт фильтром]", rules.Check(sub, At(0))!.Text);
    }

    [Fact]
    public void Gift_without_a_comment_is_left_as_is()
    {
        var rules = new ChatRules(new AppSettings { BlockedWords = "rose" });
        var gift = Chat("g", MessageMark.Paid) with { Text = "подарок: Rose ×5", Amount = "подарок: Rose ×5" };
        Assert.Equal("подарок: Rose ×5", rules.Check(gift, At(0))!.Text);
    }
}
