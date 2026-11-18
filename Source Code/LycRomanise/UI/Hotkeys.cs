using System.Runtime.InteropServices;
using System.Windows.Interop;
using LycRomanise.Core;

namespace LycRomanise.UI;

/// <summary>System-wide shortcuts, through RegisterHotKey on a message-only window.</summary>
public sealed class Hotkeys : IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    const uint Alt = 1, Ctrl = 2, NoRepeat = 0x4000;
    const int WM_HOTKEY = 0x0312;

    readonly HwndSource _source;
    readonly Dictionary<int, Action> _actions = new();
    int _next = 1;

    public static readonly (string keys, string what)[] Defaults =
    {
        ("Ctrl + Alt + L", "Open or close the full lyrics window"),
        ("Ctrl + Alt + K", "Lock or unlock the strip"),
        ("Ctrl + Alt + H", "Show or hide the strip"),
        ("Ctrl + Alt + ← / →", "This song's lyrics earlier / later"),
        ("Ctrl + Alt + Space", "Play / pause"),
    };

    public Hotkeys()
    {
        _source = new HwndSource(new HwndSourceParameters("LycRomanise.Hotkeys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _source.AddHook(Hook);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a)) { handled = true; a(); }
        return IntPtr.Zero;
    }

    public void Register(uint vk, Action action)
    {
        int id = _next++;
        if (RegisterHotKey(_source.Handle, id, Ctrl | Alt | NoRepeat, vk)) _actions[id] = action;
        else Log.Info($"hotkey 0x{vk:X} is taken by another app");
    }

    public void Clear()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    public void Dispose()
    {
        Clear();
        _source.Dispose();
    }
}
