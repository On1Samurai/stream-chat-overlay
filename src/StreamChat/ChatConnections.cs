using System.Windows.Threading;
using StreamChat.Core;
using StreamChat.Core.Chat;
using StreamChat.Core.Donations;

namespace StreamChat;

/// <summary>Держит подключения и переносит их события в поток интерфейса.</summary>
internal sealed class ChatConnections : IDisposable
{
    private static readonly TimeSpan AfterCrash = TimeSpan.FromSeconds(30);

    private sealed class Connection
    {
        public CancellationTokenSource? Running;
        public string? Signature;
        public string Target = "";
    }

    private readonly Dispatcher _ui;
    private readonly Action<ChatEvent> _onEvent;
    private readonly Action<Platform, SourceStatus> _onStatus;
    private readonly Action<string> _onRoom;
    private readonly Action<long> _onTikTokViewers;
    private readonly Connection _twitch = new();
    private readonly Connection _youTube = new();
    private readonly Connection _tikTok = new();
    private readonly Connection _donations = new();
    private readonly YouTubeApi _api = new();

    public ChatConnections(Dispatcher ui, Action<ChatEvent> onEvent, Action<Platform, SourceStatus> onStatus, Action<string> onRoom,
        Action<long> onTikTokViewers)
    {
        _ui = ui;
        _onEvent = onEvent;
        _onStatus = onStatus;
        _onRoom = onRoom;
        _onTikTokViewers = onTikTokViewers;
    }

    /// <param name="force">Переподключить, даже если настройки те же.</param>
    public void Connect(AppSettings settings, bool force)
    {
        ConnectTwitch(settings.TwitchChannel.Trim(), force);
        ConnectYouTube(settings, force);
        ConnectTikTok(settings.TikTokEnabled, force);
        ConnectDonations(settings.DonationTokenProtected, force);
    }

    public void Dispose()
    {
        _twitch.Running?.Cancel();
        _youTube.Running?.Cancel();
        _tikTok.Running?.Cancel();
        _donations.Running?.Cancel();
        _api.Dispose();
    }

    private void ConnectTwitch(string input, bool force)
    {
        if (!force && input == _twitch.Signature) return;
        _twitch.Signature = input;

        var channel = ChatLinks.TwitchChannel(input);
        Stop(Platform.Twitch, _twitch, channel ?? "");
        if (input.Length == 0)
            _onStatus(Platform.Twitch, new(SourceHealth.Off, "не настроен"));
        else if (channel is null)
            _onStatus(Platform.Twitch, new(SourceHealth.Error, "имя канала написано неверно"));
        else
            Start(Platform.Twitch, _twitch, (sink, cancel) => new TwitchClient().RunAsync(channel, sink, cancel));
    }

    private void ConnectDonations(string stored, bool force)
    {
        var token = Secret.Unprotect(stored);
        var signature = token ?? "?";
        if (!force && signature == _donations.Signature) return;
        _donations.Signature = signature;

        Stop(Platform.DonationAlerts, _donations, token ?? "");
        if (token is null)
            _onStatus(Platform.DonationAlerts, new(SourceHealth.Error, "вход с другого ПК, войдите заново"));
        else if (token.Length == 0)
            _onStatus(Platform.DonationAlerts, new(SourceHealth.Off, "вход не выполнен"));
        else
            Start(Platform.DonationAlerts, _donations, (sink, cancel) => new DonationAlertsClient().RunAsync(token, sink, cancel));
    }

    private void ConnectTikTok(bool enabled, bool force)
    {
        var signature = enabled ? "on" : "off";
        if (!force && signature == _tikTok.Signature) return;
        _tikTok.Signature = signature;

        Stop(Platform.TikTok, _tikTok, enabled ? "on" : "");
        if (enabled) Start(Platform.TikTok, _tikTok, (sink, cancel) => new TikFinityClient().RunAsync(sink, cancel));
        else _onStatus(Platform.TikTok, new(SourceHealth.Off, "не настроен"));
    }

    private void ConnectYouTube(AppSettings settings, bool force)
    {
        var link = settings.YouTubeLink.Trim();
        // шифротекст одного ключа каждый раз разный, сравнивать можно только расшифрованный
        var key = Secret.Unprotect(settings.YouTubeKeyProtected)?.Trim();
        var signature = $"{link}\n{key ?? "?"}\n{settings.YouTubeIntervalSeconds}\n{settings.YouTubeTermsAccepted}";
        if (!force && signature == _youTube.Signature) return;
        _youTube.Signature = signature;

        var video = ChatLinks.YouTubeVideo(link);
        Stop(Platform.YouTube, _youTube, video ?? "");

        SourceStatus? problem =
            link.Length == 0 ? new(SourceHealth.Off, "не настроен")
            : video is null ? new(SourceHealth.Error, "нужна ссылка на сам эфир, а не на канал")
            : key is null ? new(SourceHealth.Error, "ключ с другого ПК, введите его заново")
            : key.Length == 0 ? new(SourceHealth.Error, "не указан API-ключ")
            : !settings.YouTubeTermsAccepted ? new(SourceHealth.Error, "примите условия YouTube на вкладке «Чат»")
            : null;
        if (problem is not null)
        {
            _onStatus(Platform.YouTube, problem);
            return;
        }

        var interval = TimeSpan.FromSeconds(settings.YouTubeIntervalSeconds);
        Start(Platform.YouTube, _youTube, (sink, cancel) => new YouTubeClient(_api, interval).RunAsync(video!, key!, sink, cancel));
    }

    private void Stop(Platform platform, Connection connection, string target)
    {
        connection.Running?.Cancel();
        connection.Running = null;
        if (connection.Target == target) return;
        // чужой канал: прежние сообщения к нему не относятся
        connection.Target = target;
        _onEvent(new Cleared(platform));
        if (platform == Platform.Twitch) _onRoom("");
    }

    private void Start(Platform platform, Connection connection, Func<ChatSink, CancellationToken, Task> run)
    {
        var running = new CancellationTokenSource();
        connection.Running = running;
        var cancel = running.Token;

        // опоздавшие события отменённого подключения отбрасываются уже в потоке интерфейса
        void Post(Action action) => _ui.BeginInvoke(() =>
        {
            if (!cancel.IsCancellationRequested) action();
        });

        var sink = new ChatSink(
            change => Post(() => _onEvent(change)),
            status => Post(() => _onStatus(platform, status)),
            room => Post(() => _onRoom(room)),
            count => Post(() => _onTikTokViewers(count)));

        _ = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await run(sink, cancel).ConfigureAwait(false);
                    return;
                }
                catch (Exception e) when (!cancel.IsCancellationRequested)
                {
                    // только тип: в Message может быть текст зрителя или адрес
                    sink.Status(new(SourceHealth.Waiting, $"сбой подключения ({e.GetType().Name}), повторю"));
                }
                catch (Exception)
                {
                    return; // отменено
                }

                try
                {
                    await Task.Delay(AfterCrash, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }
}
