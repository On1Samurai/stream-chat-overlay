using System.Reflection;
using System.Windows.Threading;
using StreamChat.Core.Updates;

namespace StreamChat;

internal sealed class UpdateNotifier : IDisposable
{
    private static readonly TimeSpan AfterStart = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Between = TimeSpan.FromHours(24);

    private readonly Dispatcher _ui;
    private readonly Action<UpdateResult> _onResult;
    private readonly UpdateChecker _checker = new();
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _schedule;
    private bool _checking;

    public UpdateNotifier(Dispatcher ui, Action<UpdateResult> onResult)
    {
        _ui = ui;
        _onResult = onResult;
    }

    public static string CurrentVersion { get; } =
        typeof(UpdateNotifier).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    public void SetEnabled(bool enabled)
    {
        if (enabled == _schedule is not null) return;
        if (!enabled)
        {
            _schedule?.Cancel();
            _schedule = null;
            return;
        }

        var schedule = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _schedule = schedule;
        var cancel = schedule.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AfterStart, cancel).ConfigureAwait(false);
                while (true)
                {
                    _ = _ui.BeginInvoke(() =>
                    {
                        if (!cancel.IsCancellationRequested) CheckNow();
                    });
                    await Task.Delay(Between, cancel).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public void CheckNow()
    {
        if (_checking || _stop.IsCancellationRequested) return;
        _checking = true;
        var cancel = _stop.Token;
        _ = Task.Run(async () =>
        {
            var result = new UpdateResult(UpdateState.Failed);
            try
            {
                result = await _checker.CheckAsync(CurrentVersion, cancel).ConfigureAwait(false);
            }
            finally
            {
                _ = _ui.BeginInvoke(() =>
                {
                    _checking = false;
                    if (!cancel.IsCancellationRequested) _onResult(result);
                });
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _checker.Dispose();
    }
}
