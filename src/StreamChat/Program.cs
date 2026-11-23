using System.Windows;

namespace StreamChat;

internal static class Program
{
    private const string InstanceSignal = @"Local\StreamChat.Show";

    [STAThread]
    private static void Main()
    {
        // зависшее окно Windows подменяет своей копией, а на копию защита от захвата не переносится
        NativeMethods.DisableProcessWindowsGhosting();

        EventWaitHandle instance;
        bool first;
        try
        {
            instance = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceSignal, out first);
        }
        catch (UnauthorizedAccessException)
        {
            // уже запущен от администратора: сигнал ему послать нельзя
            return;
        }

        using var signal = instance;
        if (!first)
        {
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
        app.Startup += (_, _) => controller.Start();
        app.SessionEnding += (_, _) => controller.Dispose();

        var wait = ThreadPool.RegisterWaitForSingleObject(signal,
            (_, _) => app.Dispatcher.BeginInvoke(controller.OpenSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        app.Run();
        wait.Unregister(null);
    }
}
