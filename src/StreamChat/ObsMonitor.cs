using System.Windows.Threading;
using StreamChat.Core;
using StreamChat.Core.Chat;
using StreamChat.Core.Obs;

namespace StreamChat;

internal sealed class ObsMonitor : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly Action<ObsSnapshot> _onSnapshot;
    private readonly Action<SourceStatus> _onStatus;
    private CancellationTokenSource? _running;
    private string? _signature;

    public ObsMonitor(Dispatcher ui, Action<ObsSnapshot> onSnapshot, Action<SourceStatus> onStatus)
    {
        _ui = ui;
        _onSnapshot = onSnapshot;
        _onStatus = onStatus;
    }

    public void Connect(AppSettings settings, bool force)
    {
        var password = Secret.Unprotect(settings.ObsPasswordProtected) ?? "";
        var microphone = settings.ObsMicrophone.Trim();
        var signature = settings.ObsEnabled ? $"{settings.ObsPort}\n{password}\n{microphone}" : "";
        if (!force && signature == _signature) return;
        _signature = signature;

        _running?.Cancel();
        _running = null;
        if (!settings.ObsEnabled)
        {
            _onStatus(new(SourceHealth.Off, "выключено"));
            return;
        }

        var running = new CancellationTokenSource();
        _running = running;
        var cancel = running.Token;

        void Post(Action action) => _ui.BeginInvoke(() =>
        {
            if (!cancel.IsCancellationRequested) action();
        });

        // только этот ПК: пароль и состояние OBS по сети не ходят
        var client = new ObsClient(new Uri($"ws://localhost:{settings.ObsPort}/"), password, microphone);
        _ = Task.Run(async () =>
        {
            try
            {
                await client.RunAsync(
                    snapshot => Post(() => _onSnapshot(snapshot)),
                    status => Post(() => _onStatus(status)),
                    cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (!cancel.IsCancellationRequested)
            {
                Post(() => _onStatus(new(SourceHealth.Error, $"сбой ({e.GetType().Name}), нажмите «Подключить»")));
            }
            catch (Exception)
            {
                // отменено
            }
        });
    }

    public void Dispose() => _running?.Cancel();
}
