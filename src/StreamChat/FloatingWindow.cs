using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StreamChat.Core;

namespace StreamChat;

/// <summary>Окно поверх игры: клики насквозь, без фокуса, место из настроек, перетаскивание по горячей клавише.</summary>
internal abstract class FloatingWindow : ProtectedWindow
{
    private const byte EditingAlpha = 235;

    protected static readonly Brush Accent = Palette.Brush("#4FE3C1");
    private static readonly Brush EditingPanel = Palette.Brush("#101318");

    private readonly Func<AppSettings, Placement> _choosePlacement;
    private readonly Border _frame;
    private readonly TextBlock _hint;
    private readonly DockPanel _content = new();

    // рамка 2 и поля 8, 6, 12, 6: место считается по содержимому без них
    private static readonly Thickness Inset = new(10, 8, 14, 8);
    private UIElement? _body;
    private nint _returnFocusTo;
    private (double Left, double Top) _editStart;

    public event Action<Placement>? PlacementChanged;

    protected FloatingWindow(AppSettings settings, Func<AppSettings, Placement> choosePlacement)
    {
        _choosePlacement = choosePlacement;
        Settings = settings;
        Placement = choosePlacement(settings);

        Title = "StreamChat";
        WindowStyle = WindowStyle.None;
        // true включает UpdateLayeredWindow, с ним защита от захвата падает на Windows 10
        AllowsTransparency = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // точные края для вырезаемого цвета
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _hint = new TextBlock
        {
            Foreground = Accent,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            Visibility = Visibility.Collapsed,
        };
        DockPanel.SetDock(_hint, Dock.Top);
        _content.Children.Add(_hint);

        // рамка всегда той же толщины, чтобы текст не прыгал при входе в режим настройки
        _frame = new Border
        {
            BorderThickness = new Thickness(2),
            Padding = new Thickness(Inset.Left - 2, Inset.Top - 2, Inset.Right - 2, Inset.Bottom - 2),
            Child = _content,
        };
        _frame.MouseLeftButtonDown += (_, _) =>
        {
            if (Editing) DragMove();
        };

        Root.Children.Add(_frame);
        Content = Root;
    }

    protected Grid Root { get; } = new();
    protected AppSettings Settings { get; private set; }
    protected Placement Placement { get; private set; }
    protected bool Editing { get; private set; }

    protected abstract string Hint { get; }

    /// <summary>Ширину меняли мышью и её надо запомнить.</summary>
    protected virtual bool WasResized => false;

    protected bool HasCutOut => !Editing && Settings.Background != BackgroundStyle.Panel;

    protected override byte Alpha => Editing ? EditingAlpha : (byte)Math.Round(Settings.Opacity * 255 / 100d);

    // при перетаскивании фон сплошной: сквозь вырез мышь провалилась бы в игру
    protected override Rgb? CutOutColor => HasCutOut ? Settings.TransparentColor : null;

    public override CaptureResult ShowProtected()
    {
        var result = base.ShowProtected();
        if (result.Protected) KeepOnTop();
        return result;
    }

    public void Apply(AppSettings settings)
    {
        Settings = settings;
        var placement = _choosePlacement(settings);
        if (placement != Placement)
        {
            Placement = placement;
            OnPlacementApplied();
        }

        Restyle();
        RefreshLayer();
        Reposition();
    }

    public void KeepOnTop() =>
        NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopmost, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);

    /// <summary>Держит окно на месте при смене размера и разрешения игры.</summary>
    public void Reposition()
    {
        if (!Editing) MoveToPlacement();
    }

    public void SetEditing(bool editing)
    {
        if (Editing == editing) return;
        if (editing) _returnFocusTo = NativeMethods.GetForegroundWindow();
        // до того как спрячется подсказка: нижний край считается вместе с ней
        else RememberDrag();
        Editing = editing;

        _hint.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        OnEditingChanged();
        Restyle();
        if (Handle == 0) return;

        ApplyStyle();
        RefreshLayer();
        if (IsVisible) MoveToPlacement();

        // NOACTIVATE не забирает уже полученный фокус, игре его надо вернуть явно
        if (!editing && _returnFocusTo != 0 && NativeMethods.GetForegroundWindow() == Handle)
            NativeMethods.SetForegroundWindow(_returnFocusTo);
    }

    protected void SetBody(UIElement body)
    {
        _body = body;
        _content.Children.Add(body);
    }

    /// <summary>Вызвать в конце конструктора наследника.</summary>
    protected void Restyle()
    {
        var panel = Editing ? EditingPanel
            : HasCutOut ? Palette.Brush(Settings.TransparentColor)
            : Palette.Brush(Settings.BackgroundColor);

        Background = panel;
        _frame.Background = panel;
        _frame.BorderBrush = Editing ? Accent : panel;
        // цветное сглаживание ClearType на вырезанном фоне даёт радужную кайму
        TextOptions.SetTextRenderingMode(_frame, HasCutOut ? TextRenderingMode.Grayscale : TextRenderingMode.Auto);
        _hint.Text = Hint;
        RenderContent();
    }

    protected abstract void RenderContent();

    protected virtual void OnPlacementApplied()
    {
    }

    protected virtual void OnEditingChanged()
    {
    }

    protected void LimitHintWidth(double width) => _hint.MaxWidth = width;

    protected override void OnShownTransparent() => MoveToPlacement();

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (Handle != 0) Reposition();
    }

    protected override int AdjustStyle(int style)
    {
        style &= ~(NativeMethods.WsExAppWindow | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate);
        style |= NativeMethods.WsExToolWindow;
        if (!Editing) style |= NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate;
        return style;
    }

    protected override void OnMessage(int message, nint lParam)
    {
        if (message != NativeMethods.WmWindowPosChanging || Editing) return;

        // при смене DPI WPF двигает окно без NOACTIVATE и забирает фокус у игры
        var position = Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam);
        if ((position.Flags & NativeMethods.SwpHideWindow) != 0) return;
        position.Flags |= NativeMethods.SwpNoActivate;
        Marshal.StructureToPtr(position, lParam, false);
    }

    protected static bool Near(double a, double b) => Math.Abs(a - b) < 1;

    private (double Width, double Height) ScreenSize()
    {
        // SystemParameters помнит масштаб на момент входа в Windows, поэтому пиксели и DPI окна
        var dpi = VisualTreeHelper.GetDpi(this);
        return (NativeMethods.GetSystemMetrics(NativeMethods.SmCxScreen) / dpi.DpiScaleX,
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyScreen) / dpi.DpiScaleY);
    }

    private void MoveToPlacement()
    {
        if (!IsVisible) return;
        UpdateLayout();
        var screen = ScreenSize();
        var (left, top) = Placement.Locate(screen.Width, screen.Height, ActualWidth, ActualHeight);
        if (!Near(Left, left)) Left = left;
        if (!Near(Top, top)) Top = top;
        if (Editing) _editStart = (Left, Top);
    }

    private void RememberDrag()
    {
        if (!IsVisible) return;
        var moved = !Near(Left, _editStart.Left) || !Near(Top, _editStart.Top);
        var resized = WasResized;
        if (!moved && !resized) return;

        var width = resized ? Width : Placement.Width;

        // место по самому содержимому: подсказка режима сейчас исчезнет, и окно станет меньше
        var origin = _body?.TranslatePoint(new Point(0, 0), this) ?? new Point(Inset.Left, Inset.Top);
        var left = Left + origin.X - Inset.Left;
        var top = Top + origin.Y - Inset.Top;
        var height = (_body?.RenderSize.Height ?? ActualHeight) + Inset.Top + Inset.Bottom;

        // одна ширина не отвязывает окно от выбранного угла
        Placement = moved || Placement.Position == Position.Custom
            ? Placement.DraggedTo(left, top, width, height, ScreenSize().Height)
            : Placement with { Width = width };
        PlacementChanged?.Invoke(Placement);
    }
}
