using System.Windows.Threading;
using StreamChat.Core;
using StreamChat.Core.Chat;
using StreamChat.Core.Viewers;

namespace StreamChat;

/// <summary>Раз в минуту спрашивает число зрителей Twitch и YouTube. У TikTok оно приходит само.</summary>
internal sealed class ViewerPolling : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private sealed class Poll
    {
        public string Target = "";
        public CancellationTokenSource? Running;
    }

    private readonly Dispatcher _ui;
    private readonly Action<Platform, ViewerReading> _onReading;
    private readonly Action<Platform> _onTargetChanged;
    private readonly TwitchViewers _twitchApi = new();
    private readonly YouTubeApi _youTubeApi = new();
    private readonly Poll _twitch = new();
    private readonly Poll _youTube = new();

    public ViewerPolling(Dispatcher ui, Action<Platform, ViewerReading> onReading, Action<Platform> onTargetChanged)
    {
        _ui = ui;
        _onReading = onReading;
        _onTargetChanged = onTargetChanged;
    }

    /// <param name="connected">Каналы, к которым подключились. Не те, что сейчас печатают: каждая буква ушла бы в DecAPI.</param>
    /// <param name="current">Галочки из настроек.</param>
    public void Update(AppSettings connected, AppSettings current)
    {
        var channel = current.ShowTwitchViewers ? ChatLinks.TwitchChannel(connected.TwitchChannel) ?? "" : "";
        Set(Platform.Twitch, _twitch, channel, cancel => _twitchApi.GetAsync(channel, cancel));

        var video = "";
        var key = "";
        if (current.ShowYouTubeViewers && connected.YouTubeTermsAccepted)
        {
            video = ChatLinks.YouTubeVideo(connected.YouTubeLink) ?? "";
            key = Secret.Unprotect(connected.YouTubeKeyProtected)?.Trim() ?? "";
            if (key.Length == 0 || key.Any(c => c is <= ' ' or > '~')) video = "";
        }

        Set(Platform.YouTube, _youTube, video.Length == 0 ? "" : $"{video}\n{key}",
            cancel => _youTubeApi.GetViewersAsync(video, key, cancel));
    }

    public void Dispose()
    {
        _twitch.Running?.Cancel();
        _youTube.Running?.Cancel();
        _twitchApi.Dispose();
        _youTubeApi.Dispose();
    }

    private void Set(Platform platform, Poll poll, string target, Func<CancellationToken, Task<ViewerReading>> get)
    {
        if (target == poll.Target) return;
        poll.Target = target;
        poll.Running?.Cancel();
        poll.Running = null;
        _onTargetChanged(platform);
        if (target.Length == 0) return;

        var running = new CancellationTokenSource();
        poll.Running = running;
        var cancel = running.Token;
        _ = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                var reading = new ViewerReading(ViewerState.Unknown);
                try
                {
                    reading = await get(cancel).ConfigureAwait(false);
                }
                catch (Exception) when (!cancel.IsCancellationRequested)
                {
                    // любой сбой это неизвестное число, через минуту повтор
                }
                catch (Exception)
                {
                    return; // отменено
                }

                _ = _ui.BeginInvoke(() =>
                {
                    if (!cancel.IsCancellationRequested) _onReading(platform, reading);
                });

                try
                {
                    await Task.Delay(Interval, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }
}
