using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using StreamChat.Core.Updates;

namespace StreamChat;

internal static class Program
{
    private const string InstanceSignal = @"Local\StreamChat.Show";

    [STAThread]
    private static void Main(string[] args)
    {
        // новая версия после обновления: старая ещё закрывается, иначе приняли бы её за второй экземпляр
        var afterUpdate = false;
        if (args is ["--after-update", var pidText] && int.TryParse(pidText, out var pid) && pid != Environment.ProcessId)
        {
            afterUpdate = true;
            WaitForExit(pid);
        }

        // зависшее окно Windows подменяет своей копией, а на копию защита от захвата не переносится
        NativeMethods.DisableProcessWindowsGhosting();

        EventWaitHandle instance;
        bool first;
        try
        {
            instance = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceSignal, out first);
            // старая версия ещё держит событие: ждём её ещё до 15 с, сигнал ей не шлём
            for (var waited = 0; afterUpdate && !first && waited < 15_000; waited += 250)
            {
                instance.Dispose();
                Thread.Sleep(250);
                instance = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceSignal, out first);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // уже запущен от администратора: сигнал ему послать нельзя
            return;
        }

        using var signal = instance;
        if (!first)
        {
            // после обновления сигнал не шлём: он открыл бы настройки старой версии
            if (afterUpdate) return;
            // без MessageBox: его увидели бы зрители, просто открываем настройки уже запущенного чата
            // иначе настройки первого экземпляра откроются позади игры
            NativeMethods.AllowSetForegroundWindow(NativeMethods.AnyProcess);
            signal.Set();
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var controller = new ChatController(app.Shutdown);

        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            controller.Fail(e.Exception);
        };
        // исключения из меню трея идут мимо Dispatcher, без этого WinForms покажет окно ошибки в эфир
        System.Windows.Forms.Application.ThreadException += (_, e) => controller.Fail(e.Exception);
        app.Startup += (_, _) =>
        {
            controller.Start();
            RemoveUpdateLeftovers();
        };
        app.SessionEnding += (_, _) => controller.Dispose();

        var wait = ThreadPool.RegisterWaitForSingleObject(signal,
            (_, _) => app.Dispatcher.BeginInvoke(controller.OpenSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        app.Run();
        wait.Unregister(null);
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // процесса уже нет или ждать его нельзя: запускаемся сразу
        }
    }

    private static void RemoveUpdateLeftovers()
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            UpdateInstaller.Cleanup(exe);
        }
        catch (Exception)
        {
            // остатки не мешают работе, удалим при следующем запуске
        }
    }
}
