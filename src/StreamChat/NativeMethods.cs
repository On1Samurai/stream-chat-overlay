using System.Runtime.InteropServices;

namespace StreamChat;

internal static class NativeMethods
{
    public const int SmCxScreen = 0;
    public const int SmCyScreen = 1;
    public const int GwlExStyle = -20;
    public const int WmWindowPosChanging = 0x0046;
    public const int WmStyleChanging = 0x007C;
    public const int WmHotKey = 0x0312;

    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExAppWindow = 0x00040000;
    public const int WsExLayered = 0x00080000;
    public const int WsExNoActivate = 0x08000000;

    public const uint WdaExcludeFromCapture = 0x11;
    public const uint LwaColorKey = 0x1;
    public const uint LwaAlpha = 0x2;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpHideWindow = 0x0080;
    public const uint SwpShowWindow = 0x0040;
    public const int SwRestore = 9;
    public const int AnyProcess = -1;
    public static readonly nint HwndTopmost = -1;
    public static readonly nint HwndMessageOnly = -3;

    public const uint ModNoRepeat = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct StyleStruct
    {
        public int Old;
        public int New;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowPos
    {
        public nint Window;
        public nint After;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern void DisableProcessWindowsGhosting();

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(nint window, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(nint window, int id);
}
