using System.Windows.Media;
using StreamChat.Core;
using StreamChat.Core.Obs;

namespace StreamChat;

internal static class Palette
{
    private static readonly Brush Twitch = Brush("#BF9BFF");
    private static readonly Brush YouTube = Brush("#FF8585");
    private static readonly Brush TikTok = Brush("#69C9D0");
    private static readonly Brush Donations = Brush("#FFB347");
    private static readonly Brush App = Brush("#4FE3C1");
    private static readonly Brush Mention = Brush("#6CB6FF");
    private static readonly Brush Newcomer = Brush("#7BE36B");
    private static readonly Brush Money = Brush("#FFD84D");
    private static readonly Brush Raid = Brush("#FF8AD8");
    private static readonly Brush Good = Brush("#7BE36B");
    private static readonly Brush Warning = Brush("#FFD84D");
    private static readonly Brush Bad = Brush("#FF5C5C");
    private static readonly Brush Muted = Brush("#B8C0CC");

    public static (Brush Color, string Label) Of(Platform platform) => platform switch
    {
        Platform.Twitch => (Twitch, "TW"),
        Platform.YouTube => (YouTube, "YT"),
        Platform.TikTok => (TikTok, "TT"),
        Platform.DonationAlerts => (Donations, "DA"),
        _ => (App, ""),
    };

    public static (Brush Color, string Label) Of(MessageMark mark) => mark switch
    {
        MessageMark.Mention => (Mention, "ТЕБЕ"),
        MessageMark.FirstMessage => (Newcomer, "НОВИЧОК"),
        MessageMark.Subscription => (Twitch, "ПОДПИСКА"),
        MessageMark.Raid => (Raid, "РЕЙД"),
        MessageMark.Paid => (Money, "ПЛАТНОЕ"),
        MessageMark.Donation => (Donations, "ДОНАТ"),
        _ => (App, ""),
    };

    public static Brush Of(StatusLevel level) => level switch
    {
        StatusLevel.Good => Good,
        StatusLevel.Warning => Warning,
        StatusLevel.Bad => Bad,
        _ => Muted,
    };

    public static Brush Brush(string hex) => Brush(Rgb.Parse(hex) ?? default);

    public static Brush Brush(Rgb color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
