using System.Text.RegularExpressions;

namespace StreamChat.Core.Chat;

public static partial class ChatLinks
{
    // \z, а не $: $ пропускает перевод строки и вторую команду IRC за ним
    [GeneratedRegex(@"\A[a-z0-9_]{3,25}\z")]
    private static partial Regex ChannelName();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{11}\z")]
    private static partial Regex VideoId();

    public static string? TwitchChannel(string text)
    {
        var input = text.Trim();
        var name = input.TrimStart('@', '#');
        if (input.Contains('/'))
        {
            if (Address(input) is not { } address) return null;
            if (address.Host is not ("twitch.tv" or "www.twitch.tv" or "m.twitch.tv" or "dashboard.twitch.tv")) return null;

            var path = address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            name = path switch
            {
                ["popout", "moderator" or "u", var channel, ..] => channel,
                ["popout" or "moderator" or "u" or "embed", var channel, ..] => channel,
                [var channel, ..] => channel,
                _ => "",
            };
        }

        name = name.ToLowerInvariant();
        return ChannelName().IsMatch(name) ? name : null;
    }

    public static string? YouTubeVideo(string text)
    {
        var input = text.Trim();
        if (VideoId().IsMatch(input)) return input;
        if (Address(input) is not { } address) return null;

        var path = address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var id = (address.Host, path) switch
        {
            ("youtu.be" or "www.youtu.be", [var video, ..]) => video,
            ("studio.youtube.com", ["video", var video, ..]) => video,
            ("youtube.com" or "www.youtube.com" or "m.youtube.com", ["live" or "embed" or "shorts", var video, ..]) => video,
            ("youtube.com" or "www.youtube.com" or "m.youtube.com" or "studio.youtube.com", ["watch" or "live_chat", ..]) =>
                address.Query.TrimStart('?').Split('&').FirstOrDefault(part => part.StartsWith("v=", StringComparison.Ordinal))?[2..],
            _ => null,
        };
        return id is not null && VideoId().IsMatch(id) ? id : null;
    }

    private static Uri? Address(string input) =>
        Uri.TryCreate(input.Contains("://", StringComparison.Ordinal) ? input : $"https://{input}", UriKind.Absolute, out var address)
        && address.Scheme is "https" or "http"
            ? address
            : null;
}
