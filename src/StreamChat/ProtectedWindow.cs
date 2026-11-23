using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using StreamChat.Core;

namespace StreamChat;

/// <summary>Основа всех окон программы: на экране только после подтверждённой защиты от захвата.</summary>
internal abstract class ProtectedWindow : Window, ICaptureWindow
{
    private bool _layered;
    private bool _opaque;

    public event Action? DismissRequested;

    protected nint Handle { get; private set; }

    protected virtual byte Alpha => 255;

    /// <summary>Цвет, который Windows вырезает из окна, или null.</summary>
    protected virtual Rgb? CutOutColor => null;

    public bool IsExcluded =>
        Handle != 0
        && NativeMethods.GetWindowDisplayAffinity(Handle, out var affinity)
        && affinity == NativeMethods.WdaExcludeFromCapture;

    public int Exclude()
    {
        if (NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WdaExcludeFromCapture)) return 0;
        var error = Marshal.GetLastPInvokeError();
        return error == 0 ? -1 : error;
    }

    public void RemoveLayered()
    {
        _layered = false;
        ApplyStyle();
    }

    public void MakeTransparent()
    {
        _opaque = false;
        _layered = true;
        ApplyStyle();
        SetLayer(0);
    }

    public void MakeOpaque()
    {
        SetLayer(Alpha);
        _opaque = true;
    }

    void ICaptureWindow.Hide() => Conceal();

    void ICaptureWindow.Show()
    {
        Show();
        // окно ещё полностью прозрачно: перестановку не видно
        OnShownTransparent();
    }

    /// <summary>Единственный путь к показу окна.</summary>
    public virtual CaptureResult ShowProtected()
    {
        CreateHandle();
        return CaptureGuard.Show(this);
    }

    /// <summary>Прозрачность 0 до Hide: иначе DWM может оставить кадр на экране.</summary>
    public void Conceal()
    {
        _opaque = false;
        if (Handle != 0) NativeMethods.SetLayeredWindowAttributes(Handle, 0, 0, NativeMethods.LwaAlpha);
        Hide();
    }

    protected virtual void OnShownTransparent()
    {
    }

    protected virtual int AdjustStyle(int style) => style;

    protected virtual void OnMessage(int message, nint lParam)
    {
    }

    /// <summary>Применяет новую прозрачность, не раскрывая ещё не подтверждённое окно.</summary>
    protected void RefreshLayer()
    {
        if (_opaque) SetLayer(Alpha);
    }

    protected void ApplyStyle()
    {
        var current = (int)NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GwlExStyle, RequiredStyle(current));
        NativeMethods.SetWindowPos(Handle, 0, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate
            | NativeMethods.SwpFrameChanged | NativeMethods.SwpNoZOrder);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // уничтожать защищённое окно нельзя, а Hide во время Closing WPF запрещает
        e.Cancel = true;
        Dispatcher.BeginInvoke(() => DismissRequested?.Invoke());
    }

    private void CreateHandle()
    {
        if (Handle != 0) return;

        Handle = new WindowInteropHelper(this).EnsureHandle();
        var source = HwndSource.FromHwnd(Handle)!;
        source.AddHook(OnWindowMessage);
        // маленьким окнам GPU не нужен, он нужен игре. К тому же только так цвет выреза точен
        source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        ApplyStyle();
    }

    private int RequiredStyle(int style)
    {
        style &= ~NativeMethods.WsExLayered;
        if (_layered) style |= NativeMethods.WsExLayered;
        return AdjustStyle(style);
    }

    private void SetLayer(byte alpha)
    {
        var flags = NativeMethods.LwaAlpha;
        uint colorRef = 0;
        if (CutOutColor is { } color)
        {
            flags |= NativeMethods.LwaColorKey;
            colorRef = (uint)(color.R | color.G << 8 | color.B << 16); // COLORREF: 0x00BBGGRR
        }

        if (!NativeMethods.SetLayeredWindowAttributes(Handle, colorRef, alpha, flags))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private nint OnWindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        // HwndTarget снимает WS_EX_LAYERED при каждом изменении окна; handled не пускает его к сообщению
        if (message == NativeMethods.WmStyleChanging && (int)wParam == NativeMethods.GwlExStyle)
        {
            var change = Marshal.PtrToStructure<NativeMethods.StyleStruct>(lParam);
            change.New = RequiredStyle(change.New);
            Marshal.StructureToPtr(change, lParam, false);
            handled = true;
        }
        else
        {
            OnMessage(message, lParam);
        }

        return 0;
    }
}
