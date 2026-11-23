using StreamChat.Core;
using StreamChat.Core.Chat;

namespace StreamChat.Tests.Chat;

public class TikFinityParserTests
{
    private static ChatMessage Posted(string json) => Assert.IsType<Posted>(TikFinityParser.Parse(json)).Message;

    [Fact]
    public void Chat_in_flat_format()
    {
        var message = Posted("""{"event":"chat","data":{"comment":"привет","uniqueId":"viewer_1","nickname":"Зритель","userId":"42","msgId":"m1","followRole":1}}""");
        Assert.Equal(new ChatMessage(Platform.TikTok, "Зритель", "привет") { Id = "m1", AuthorId = "42", Login = "viewer_1" }, message);
    }

    [Fact]
    public void Chat_in_nested_format()
    {
        var message = Posted("""{"event":"chat","data":{"comment":"hi","user":{"uniqueId":"v","nickname":"V","userId":7},"common":{"msgId":"m2"}}}""");
        Assert.Equal(("V", "v", "7", "m2"), (message.Author, message.Login, message.AuthorId, message.Id));
    }

    [Fact]
    public void Numeric_ids_keep_every_digit() =>
        Assert.Equal("7301234567890123456",
            Posted("""{"event":"chat","data":{"comment":"x","uniqueId":"v","msgId":7301234567890123456}}""").Id);

    [Fact]
    public void Missing_nickname_falls_back_to_login() =>
        Assert.Equal("viewer_1", Posted("""{"event":"chat","data":{"comment":"x","uniqueId":"viewer_1","nickname":""}}""").Author);

    [Fact]
    public void Text_is_cleaned() =>
        Assert.Equal("a b", Posted("""{"event":"chat","data":{"comment":"a\nb‮","uniqueId":"v"}}""").Text);

    [Fact]
    public void Finished_gift_series_shows_total() =>
        Assert.Equal("подарок: Rose ×5",
            Posted("""{"event":"gift","data":{"giftName":"Rose","giftType":1,"repeatEnd":true,"repeatCount":5,"uniqueId":"v"}}""").Text);

    [Fact]
    public void Invisible_nickname_falls_back_to_login() =>
        Assert.Equal("viewer_1", Posted("""{"event":"chat","data":{"comment":"x","uniqueId":"viewer_1","nickname":"\u3164\u2800"}}""").Author);

    [Fact]
    public void Gift_from_an_invisible_name_is_still_shown() =>
        Assert.Equal("Аноним", Posted("""{"event":"gift","data":{"giftName":"Rose","giftType":2,"nickname":"\u3164"}}""").Author);

    [Fact]
    public void Gift_without_a_name_is_still_a_gift()
    {
        var gift = Posted("""{"event":"gift","data":{"giftType":2,"repeatCount":1,"uniqueId":"v"}}""");
        Assert.Equal(("подарок", MessageMark.Paid, "подарок"), (gift.Text, gift.Mark, gift.Amount));
    }

    [Fact]
    public void Gift_amount_is_the_whole_text()
    {
        var gift = Posted("""{"event":"gift","data":{"giftName":"Rose","giftType":1,"repeatEnd":true,"repeatCount":5,"uniqueId":"v"}}""");
        Assert.Equal(gift.Text, gift.Amount);
    }

    [Theory]
    [InlineData("""{"event":"subscribe","data":{"subMonth":1,"uniqueId":"v","nickname":"V"}}""", "подписка")]
    [InlineData("""{"event":"subscribe","data":{"subMonth":7,"uniqueId":"v","nickname":"V"}}""", "подписка, 7 мес.")]
    public void Subscription(string json, string text)
    {
        var message = Posted(json);
        Assert.Equal((text, MessageMark.Subscription), (message.Text, message.Mark));
    }

    [Fact]
    public void Subscriber_sticker()
    {
        var message = Posted("""{"event":"emote","data":{"uniqueId":"v","emoteImageUrl":"https://p16-sign-va.tiktokcdn.com/s.png"}}""");
        Assert.Equal("https://p16-sign-va.tiktokcdn.com/s.png", message.Emotes[message.Text]);
    }

    [Fact]
    public void Sticker_is_not_the_viewer_avatar()
    {
        var message = Posted("""{"event":"emote","data":{"uniqueId":"v","profilePictureUrl":"https://p16-sign-va.tiktokcdn.com/me.jpg","emote":{"image":{"imageUrl":"https://p16-sign-va.tiktokcdn.com/s.png"}}}}""");
        Assert.Equal("https://p16-sign-va.tiktokcdn.com/s.png", message.Emotes[message.Text]);
    }

    [Fact]
    public void Single_gift() =>
        Assert.Equal("подарок: Lion",
            Posted("""{"event":"gift","data":{"giftName":"Lion","giftType":2,"repeatEnd":false,"repeatCount":1,"uniqueId":"v"}}""").Text);

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    public void Running_gift_series_is_not_repeated(string end) =>
        Assert.Null(TikFinityParser.Parse(
            """{"event":"gift","data":{"giftName":"Rose","giftType":1,"repeatEnd":END,"repeatCount":2,"uniqueId":"v"}}""".Replace("END", end)));

    [Theory]
    [InlineData("""{"event":"like","data":{"likeCount":5,"uniqueId":"v"}}""")]
    [InlineData("""{"event":"member","data":{"uniqueId":"v"}}""")]
    [InlineData("""{"event":"chat","data":{"comment":"","uniqueId":"v"}}""")]
    [InlineData("""{"event":"chat","data":{"comment":"x"}}""")]
    [InlineData("""{"event":"chat","data":{"comment":{"a":1},"uniqueId":"v"}}""")]
    [InlineData("""{"event":"chat","data":"text"}""")]
    [InlineData("""{"event":"chat","data":{"comment":"\ud800","uniqueId":"v"}}""")]
    [InlineData("""{"event":7}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public void Other_events_and_garbage_are_ignored(string json) => Assert.Null(TikFinityParser.Parse(json));
}
