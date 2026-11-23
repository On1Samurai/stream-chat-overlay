using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Windows.Threading;
using StreamChat.Core.Chat;
using StreamChat.Core.Donations;

namespace StreamChat;

/// <summary>Вход в DonationAlerts через браузер.</summary>
internal sealed class DonationSignIn : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(5);

    private readonly Dispatcher _ui;
    private readonly Action<string> _onToken;
    private readonly Action<SourceStatus> _onStatus;
    private CancellationTokenSource? _waiting;

    public DonationSignIn(Dispatcher ui, Action<string> onToken, Action<SourceStatus> onStatus)
    {
        _ui = ui;
        _onToken = onToken;
        _onStatus = onStatus;
    }

    /// <param name="onAir">Эфир или запись по данным OBS, null: OBS не подключена и неизвестно.</param>
    /// <param name="confirmedOffAir">Стример сам подтвердил, что эфира и записи нет.</param>
    public void Start(bool? onAir, bool confirmedOffAir)
    {
        _waiting?.Cancel();
        _waiting = null;

        // браузер покажет адрес с токеном: в эфире его увидят зрители
        if (onAir == true)
        {
            _onStatus(new(SourceHealth.Error, "сначала остановите эфир и запись: браузер покажет токен зрителям"));
            return;
        }

        if (onAir is null && !confirmedOffAir)
        {
            _onStatus(new(SourceHealth.Error, "отметьте, что сейчас нет эфира и записи"));
            return;
        }

        var waiting = new CancellationTokenSource(Patience);
        _waiting = waiting;
        var login = new DonationLogin(DonationLogin.AppClientId);
        try
        {
            var token = login.WaitAsync(waiting.Token);
            // единственный запуск чужой программы: адрес собран здесь, из ответов сети в нём ничего
            using var browser = Process.Start(new ProcessStartInfo(login.Link.AbsoluteUri) { UseShellExecute = true });
            _onStatus(new(SourceHealth.Connecting, "подтвердите вход в браузере"));
            _ = FinishAsync(token, waiting);
        }
        catch (SocketException)
        {
            _onStatus(new(SourceHealth.Error, $"порт {DonationLogin.DefaultPort} занят другой программой"));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            waiting.Cancel();
            _onStatus(new(SourceHealth.Error, "не удалось открыть браузер"));
        }
    }

    public void Dispose() => _waiting?.Cancel();

    private async Task FinishAsync(Task<string?> token, CancellationTokenSource waiting)
    {
        var value = await token.ConfigureAwait(false);
        await _ui.InvokeAsync(() =>
        {
            // начали заново, этот вход уже не нужен
            if (_waiting != waiting) return;
            _waiting = null;
            if (value is null) _onStatus(new(SourceHealth.Error, "вход не завершён за 5 минут, повторите"));
            else _onToken(value);
        });
    }
}
