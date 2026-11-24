using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StreamChat.Core.Updates;

namespace StreamChat;

// Вопрос об обновлении. Без ComboBox, подсказок и контекстных меню: их всплывающие окна не скрыть от захвата
internal sealed class UpdateWindow : ProtectedWindow
{
    private readonly TextBlock _header = new() { FontWeight = FontWeights.Bold, FontSize = 16, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _body = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _extra = new();
    private readonly WrapPanel _buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };

    public event Action? UpdateRequested;
    public event Action? LaterRequested;

    public UpdateWindow()
    {
        Title = "StreamChat: обновление";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        // клавиатура остаётся у игры, фокус окно получает только по клику
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontSize = 14;
        // меню поля ввода открывается раньше ContextMenuOpening, поэтому гасим сам правый щелчок
        PreviewMouseRightButtonUp += (_, e) => e.Handled = true;
        PreviewKeyUp += (_, e) => e.Handled |= e.Key == Key.Apps;
        PreviewKeyDown += (_, e) =>
            e.Handled |= e.Key == Key.System && e.SystemKey == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift;

        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(_header);
        layout.Children.Add(_body);
        layout.Children.Add(_extra);
        layout.Children.Add(_buttons);
        Content = layout;
    }

    public void Ask(string newVersion, string installed)
    {
        _header.Text = $"Вышла версия {newVersion}";
        SetContent($"Установлена {installed}. Настройки сохранятся, программа перезапустится.",
            Button("Позже", () => LaterRequested?.Invoke()), Button("Обновить", () => UpdateRequested?.Invoke(), bold: true));
    }

    public void Progress(string text) => SetContent(text);

    public void Failed(string reason)
    {
        _header.Text = "Обновить не удалось";
        SetContent(reason, Button("Закрыть", Close));
        // адрес, а не ссылка: открытый браузер увидели бы зрители
        _extra.Children.Add(SettingsWindow.Address(UpdateChecker.DownloadPage));
    }

    protected override void OnShownTransparent()
    {
        // по центру основного монитора: высота известна только после показа
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + (area.Height - ActualHeight) / 2;
    }

    protected override void OnMessage(int message, nint lParam)
    {
        if (message != NativeMethods.WmWindowPosChanging || !IsVisible || IsActive) return;

        var position = Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam);
        // при смене DPI WPF активирует окно и забирает фокус у игры
        const uint keep = NativeMethods.SwpShowWindow | NativeMethods.SwpHideWindow;
        if ((position.Flags & keep) != 0 || (position.Flags & NativeMethods.SwpNoZOrder) == 0) return;
        position.Flags |= NativeMethods.SwpNoActivate;
        Marshal.StructureToPtr(position, lParam, false);
    }

    private void SetContent(string text, params Button[] buttons)
    {
        _body.Text = text;
        _extra.Children.Clear();
        _buttons.Children.Clear();
        foreach (var button in buttons) _buttons.Children.Add(button);
        _buttons.Visibility = buttons.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Button Button(string title, Action click, bool bold = false)
    {
        var button = new Button
        {
            Content = title,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(6, 0, 0, 4),
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        };
        button.Click += (_, _) => click();
        return button;
    }
}
