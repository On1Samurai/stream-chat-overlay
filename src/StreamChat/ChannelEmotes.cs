using System.Windows.Threading;
using StreamChat.Core.Emotes;

namespace StreamChat;

/// <summary>Наборы 7TV, BTTV и FFZ текущего канала Twitch.</summary>
internal sealed class ChannelEmotes : IDisposable
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryPartial = TimeSpan.FromMinutes(5);

    private readonly Dispatcher _ui;
    private readonly Action<IReadOnlyDictionary<string, string>> _changed;
    private readonly ThirdPartyEmotes _api = new();
    private CancellationTokenSource? _loading;
    private string _room = "";
    private bool _enabled;

    public ChannelEmotes(Dispatcher ui, Action<IReadOnlyDictionary<string, string>> changed)
    {
        _ui = ui;
        _changed = changed;
    }

    /// <param name="room">Номер канала, пусто: канала нет.</param>
    public void SetRoom(string room)
    {
        if (room != _room) _changed(new Dictionary<string, string>());
        _room = room;
        Load();
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled == _enabled) return;
        _enabled = enabled;
        Load();
    }

    public void Dispose()
    {
        _loading?.Cancel();
        _api.Dispose();
    }

    private void Load()
    {
        _loading?.Cancel();
        if (!_enabled || _room.Length == 0) return;

        var loading = new CancellationTokenSource();
        _loading = loading;
        var (room, cancel) = (_room, loading.Token);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    if (await _api.LoadAsync(room, cancel).ConfigureAwait(false) is var (emotes, complete))
                    {
                        _ = _ui.BeginInvoke(() =>
                        {
                            if (!cancel.IsCancellationRequested) _changed(emotes);
                        });
                        // часть сервисов не ответила: показываем что есть и позже пробуем снова
                        if (complete) return;
                        await Task.Delay(RetryPartial, cancel).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Delay(RetryAfter, cancel).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }
}
