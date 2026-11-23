using StreamChat.Core;
using StreamChat.Core.Chat;

namespace StreamChat.Tests.Chat;

public class TwitchParserTests
{
    private static ChatMessage Posted(string line) => Assert.IsType<Posted>(TwitchParser.Parse(line).Event).Message;

    [Fact]
    public void Message_with_tags()
    {
        var message = Posted("@badges=;display-name=Зритель;id=abc-1;user-id=42 :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #chan :привет : всем");
        Assert.Equal(new ChatMessage(Platform.Twitch, "Зритель", "привет : всем") { Id = "abc-1", AuthorId = "42", Login = "viewer" }, message);
    }

    [Fact]
    public void Empty_display_name_falls_back_to_login() =>
        Assert.Equal("viewer", Posted("@display-name=;id=1 :viewer!v@v PRIVMSG #chan :hi").Author);

    [Fact]
    public void Action_is_unwrapped() =>
        Assert.Equal("машет рукой", Posted("@id=1 :v!v@v PRIVMSG #chan :\u0001ACTION машет рукой\u0001").Text);

    [Fact]
    public void Tag_escapes_are_decoded() =>
        Assert.Equal("a b;c\\d", Posted(@"@display-name=a\sb\:c\\d;id=1 :v!v@v PRIVMSG #chan :hi").Author);

    [Fact]
    public void Subscription_notice_shows_system_text_and_comment()
    {
        var message = Posted(@"@display-name=Fan;id=n1;login=fan;user-id=7;system-msg=Fan\ssubscribed :tmi.twitch.tv USERNOTICE #chan :ура");
        Assert.Equal("Fan subscribed: ура", message.Text);
        Assert.Equal("fan", message.Login);
    }

    [Fact]
    public void Notice_without_comment() =>
        Assert.Equal("Raid", Posted("@display-name=A;id=n2;system-msg=Raid :tmi.twitch.tv USERNOTICE #chan").Text);

    [Fact]
    public void Invisible_display_name_falls_back_to_login() =>
        Assert.Equal("viewer", Posted("@display-name=\\sㅤ;id=1 :viewer!v@v PRIVMSG #chan :hi").Author);

    [Theory]
    [InlineData("standardpayforward")]
    [InlineData("communitypayforward")]
    public void Paid_forward_subscriptions_are_subscriptions(string kind) =>
        Assert.Equal(MessageMark.Subscription,
            Posted($"@msg-id={kind};display-name=A;id=1;system-msg=gift :t USERNOTICE #chan").Mark);

    [Fact]
    public void Subscription_amount_is_the_system_text() =>
        Assert.Equal("Fan subscribed", Posted(@"@msg-id=sub;display-name=Fan;id=n1;login=fan;system-msg=Fan\ssubscribed :t USERNOTICE #chan :ура").Amount);

    [Theory]
    [InlineData("animated-message")]
    [InlineData("gigantified-emote-message")]
    public void Paid_power_ups_are_money(string kind) =>
        Assert.Equal(MessageMark.Paid, Posted($"@msg-id={kind};id=1 :v!v@v PRIVMSG #chan :hello").Mark);

    [Fact]
    public void Deleted_message() =>
        Assert.Equal(new Deleted(Platform.Twitch, "abc-1"),
            TwitchParser.Parse("@login=v;target-msg-id=abc-1 :tmi.twitch.tv CLEARMSG #chan :текст").Event);

    [Fact]
    public void Banned_user() =>
        Assert.Equal(new UserCleared(Platform.Twitch, "42"),
            TwitchParser.Parse("@ban-duration=600;target-user-id=42 :tmi.twitch.tv CLEARCHAT #chan :viewer").Event);

    [Fact]
    public void Whole_chat_cleared() =>
        Assert.Equal(new Cleared(Platform.Twitch), TwitchParser.Parse("@room-id=1 :tmi.twitch.tv CLEARCHAT #chan").Event);

    [Fact]
    public void Ping_keeps_its_payload()
    {
        var line = TwitchParser.Parse("PING :tmi.twitch.tv");
        Assert.Equal(("PING", "tmi.twitch.tv"), (line.Command, line.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("@")]
    [InlineData("@id=1")]
    [InlineData(":")]
    [InlineData("@id=1 :")]
    [InlineData(":prefix")]
    [InlineData("@id=1 :v PRIVMSG")]
    [InlineData(":tmi.twitch.tv 001 justinfan1 :Welcome")]
    public void Garbage_and_service_lines_give_no_event(string line) => Assert.Null(TwitchParser.Parse(line).Event);
}
