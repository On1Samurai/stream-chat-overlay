using StreamChat.Core;
using StreamChat.Core.Chat;
using StreamChat.Core.Rules;

namespace StreamChat.Tests.Rules;

public class ChatRulesTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    private static ChatMessage Text(string text, MessageMark mark = MessageMark.None) =>
        new(Platform.Twitch, "viewer", text) { Mark = mark };

    private static ChatMessage? Pass(AppSettings settings, string text, MessageMark mark = MessageMark.None) =>
        new ChatRules(settings).Check(Text(text, mark), At(0));

    [Theory]
    [InlineData("привет @MyChannel как дела")]
    [InlineData("mychannel, смотри")]
    [InlineData("эй MYCHANNEL!")]
    public void Own_channel_name_is_a_mention(string text) =>
        Assert.Equal(MessageMark.Mention, Pass(new AppSettings { TwitchChannel = "https://twitch.tv/MyChannel" }, text)!.Mark);

    [Theory]
    [InlineData("mychannel2 лучше")]
    [InlineData("notmychannel")]
    public void Part_of_another_word_is_not_a_mention(string text) =>
        Assert.Equal(MessageMark.None, Pass(new AppSettings { TwitchChannel = "mychannel" }, text)!.Mark);

    [Fact]
    public void Extra_names_are_mentions() =>
        Assert.Equal(MessageMark.Mention, Pass(new AppSettings { MentionNames = "Саня, Санёк" }, "санёк, слева!")!.Mark);

    [Fact]
    public void Payment_mark_is_stronger_than_mention() =>
        Assert.Equal(MessageMark.Paid, Pass(new AppSettings { MentionNames = "саня" }, "саня держи", MessageMark.Paid)!.Mark);

    [Theory]
    [InlineData("спойлер", "тут СПОЙЛЕР!")]
    [InlineData("казино*", "заходи в казиношку")]
    [InlineData("бесплатные скины", "раздаю бесплатные скины тут")]
    [InlineData("a, спойлер", "спойлер")]
    public void Blocked_words_hide_message(string words, string text) =>
        Assert.Null(Pass(new AppSettings { BlockedWords = words }, text));

    [Theory]
    [InlineData("кот", "который час")]
    [InlineData("(.*)", "любой текст")]
    [InlineData("*", "любой текст")]
    [InlineData(" , ,", "любой текст")]
    public void Other_text_is_not_blocked(string words, string text) =>
        Assert.NotNull(Pass(new AppSettings { BlockedWords = words }, text));

    [Fact]
    public void Money_is_never_lost_to_the_word_filter()
    {
        var message = Pass(new AppSettings { BlockedWords = "спойлер" }, "спойлер", MessageMark.Donation)!;
        Assert.Equal((MessageMark.Donation, "[текст скрыт фильтром]"), (message.Mark, message.Text));
    }

    [Fact]
    public void Hidden_donation_keeps_its_amount()
    {
        var rules = new ChatRules(new AppSettings { BlockedWords = "спойлер" });
        var message = rules.Check(Text("[500 RUB] спойлер", MessageMark.Donation) with { Amount = "[500 RUB]" }, At(0))!;
        Assert.Equal("[500 RUB] [текст скрыт фильтром]", message.Text);
    }

    [Theory]
    [InlineData("заходи https://spam.example/x")]
    [InlineData("www.spam.example")]
    [InlineData("тут bit.ly/abc")]
    [InlineData("discord.gg/abc")]
    [InlineData("сайт skins-free.ru")]
    public void Links_are_hidden(string text) => Assert.Null(Pass(new AppSettings { HideLinks = true }, text));

    [Theory]
    [InlineData("ну и ну...да ладно")]
    [InlineData("версия 1.2.3")]
    [InlineData("и т.д.")]
    [InlineData("gg.wp")]
    public void Dots_are_not_links(string text) => Assert.NotNull(Pass(new AppSettings { HideLinks = true }, text));

    [Fact]
    public void Repeat_within_30_seconds_is_hidden()
    {
        var rules = new ChatRules(new AppSettings { HideRepeats = true });
        Assert.NotNull(rules.Check(Text("купи  скины"), At(0)));
        Assert.Null(rules.Check(Text("КУПИ скины"), At(29)));
        Assert.NotNull(rules.Check(Text("купи скины"), At(61)));
    }

    [Fact]
    public void Paid_repeats_are_shown()
    {
        var rules = new ChatRules(new AppSettings { HideRepeats = true });
        Assert.NotNull(rules.Check(Text("подарок: Rose", MessageMark.Paid), At(0)));
        Assert.NotNull(rules.Check(Text("подарок: Rose", MessageMark.Paid), At(1)));
    }

    [Fact]
    public void State_uses_the_rules()
    {
        var state = new OverlayState();
        state.SetRules(new ChatRules(new AppSettings { BlockedWords = "спам" }));
        state.Apply(new Posted(Text("спам") with { Id = "1" }), At(0));
        Assert.Empty(state.Messages);
        Assert.False(state.ShouldShow(At(0)));
    }

    [Theory]
    [InlineData("@first-msg=1;id=1 :v!v@v PRIVMSG #c :привет", MessageMark.FirstMessage)]
    [InlineData("@first-msg=0;id=1 :v!v@v PRIVMSG #c :привет", MessageMark.None)]
    [InlineData("@bits=100;id=1 :v!v@v PRIVMSG #c :Cheer100", MessageMark.Paid)]
    [InlineData("@msg-id=resub;display-name=A;id=1;system-msg=x :t USERNOTICE #c", MessageMark.Subscription)]
    [InlineData("@msg-id=submysterygift;display-name=A;id=1;system-msg=x :t USERNOTICE #c", MessageMark.Subscription)]
    [InlineData("@msg-id=raid;display-name=A;id=1;system-msg=x :t USERNOTICE #c", MessageMark.Raid)]
    [InlineData("@msg-id=announcement;display-name=A;id=1 :t USERNOTICE #c :hi", MessageMark.None)]
    public void Twitch_events_are_marked(string line, MessageMark mark) =>
        Assert.Equal(mark, Assert.IsType<Posted>(TwitchParser.Parse(line).Event).Message.Mark);

    [Fact]
    public void TikTok_gift_is_paid() =>
        Assert.Equal(MessageMark.Paid, Assert.IsType<Posted>(TikFinityParser.Parse(
            """{"event":"gift","data":{"giftName":"Rose","giftType":2,"uniqueId":"v"}}""")).Message.Mark);
}
