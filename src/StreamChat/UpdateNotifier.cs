using System.Reflection;
using System.Windows.Threading;
using StreamChat.Core.Updates;

namespace StreamChat;

internal sealed class UpdateNotifier : IDisposable
{
    private static readonly TimeSpan AfterStart = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Between = TimeSpan.FromHours(24);
    // при автозапуске сети в первые секунды может не быть: иначе следующая попытка только через сутки
    private static readonly TimeSpan[] Retries = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];
    // позже стример уже может быть в игре: найденное считается ежесуточной находкой
    private static readonly TimeSpan FirstWindow = TimeSpan.FromMinutes(10);

    private readonly Dispatcher _ui;
    private readonly Action<UpdateResult, UpdateCheckSource> _onResult;
    private readonly UpdateChecker _checker = new();
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _schedule;
    private bool _checking;
    private bool _succeeded;
    private bool _manualWanted;
    private readonly long _started = Environment.TickCount64;

    public UpdateNotifier(Dispatcher ui, Action<UpdateResult, UpdateCheckSource> onResult)
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
                Scheduled(cancel, retry: false);
                foreach (var pause in Retries)
                {
                    await Task.Delay(pause, cancel).ConfigureAwait(false);
                    Scheduled(cancel, retry: true);
                }

                while (true)
                {
                    await Task.Delay(Between, cancel).ConfigureAwait(false);
                    Scheduled(cancel, retry: false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>Повтор нужен, только пока ни одна проверка не удалась.</summary>
    private void Scheduled(CancellationToken cancel, bool retry) => _ = _ui.BeginInvoke(() =>
    {
        if (!cancel.IsCancellationRequested && !(retry && _succeeded)) CheckNow(UpdateCheckSource.Daily);
    });

    /// <param name="source">Daily для проверки по таймеру: первая удачная в первые 10 минут после запуска станет FirstAfterStart.</param>
    public void CheckNow(UpdateCheckSource source)
    {
        if (_stop.IsCancellationRequested) return;
        if (_checking)
        {
            // кнопка во время проверки по таймеру: её результат и будет ответом на кнопку
            _manualWanted |= source == UpdateCheckSource.Manual;
            return;
        }

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
                    if (cancel.IsCancellationRequested) return;
                    if (_manualWanted) source = UpdateCheckSource.Manual;
                    _manualWanted = false;
                    // неудачная проверка первой не считается
                    if (source == UpdateCheckSource.Daily && !_succeeded
                        && TimeSpan.FromMilliseconds(Environment.TickCount64 - _started) <= FirstWindow)
                        source = UpdateCheckSource.FirstAfterStart;
                    if (result.State != UpdateState.Failed) _succeeded = true;
                    _onResult(result, source);
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
