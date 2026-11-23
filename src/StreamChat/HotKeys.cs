using System.Windows.Input;
using System.Windows.Interop;
using StreamChat.Core;

namespace StreamChat;

internal sealed class HotKeys : IDisposable
{
    private readonly HwndSource _receiver;
    private readonly Dictionary<int, Action> _actions = [];

    public HotKeys()
    {
        _receiver = new HwndSource(new HwndSourceParameters("StreamChat.HotKeys")
        {
            ParentWindow = NativeMethods.HwndMessageOnly,
            WindowStyle = 0,
        });
        _receiver.AddHook(OnMessage);
    }

    public static string Describe(KeyChord chord)
    {
        var parts = new List<string>();
        if ((chord.Modifiers & KeyChord.Control) != 0) parts.Add("Ctrl");
        if ((chord.Modifiers & KeyChord.Alt) != 0) parts.Add("Alt");
        if ((chord.Modifiers & KeyChord.Shift) != 0) parts.Add("Shift");

        // у цифр имя в WPF "D1", а не "1"
        parts.Add(chord.Key is >= '0' and <= '9'
            ? ((char)chord.Key).ToString()
            : KeyInterop.KeyFromVirtualKey((int)chord.Key).ToString());
        return string.Join("+", parts);
    }

    public bool Register(KeyChord chord, Action action)
    {
        var id = _actions.Count + 1;
        _actions[id] = action;
        return NativeMethods.RegisterHotKey(_receiver.Handle, id, chord.Modifiers | NativeMethods.ModNoRepeat, chord.Key);
    }

    public void Clear()
    {
        foreach (var id in _actions.Keys) NativeMethods.UnregisterHotKey(_receiver.Handle, id);
        _actions.Clear();
    }

    public void Dispose()
    {
        Clear();
        _receiver.Dispose();
    }

    private nint OnMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != NativeMethods.WmHotKey || !_actions.TryGetValue((int)wParam, out var action)) return 0;
        handled = true;
        action();
        return 0;
    }
}
