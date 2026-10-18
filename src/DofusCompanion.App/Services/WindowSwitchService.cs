using System.Runtime.InteropServices;
using System.Windows.Interop;
using DofusCompanion.Application.Switching;
using Microsoft.Extensions.Logging;

namespace DofusCompanion.App.Services;

/// <summary>
/// Opt-in TAB switch for multi-account players: while a Dofus window is in front and at least two are open, TAB
/// brings the next character's window to the front and Shift+TAB the previous one. One key press = one window
/// change, exactly like clicking the window in the taskbar: no input is sent to the game.
/// TAB is taken only while a Dofus window is in front (it is released as soon as another program is), so
/// every other program keeps its TAB key.
/// </summary>
public sealed class WindowSwitchService : IDisposable
{
    private const int NextId = 0x0E00, PreviousId = 0x0E01;

    private readonly ILogger<WindowSwitchService> _logger;
    private readonly HwndSource _source;
    private readonly WindowCycle _cycle = new();
    private readonly NativeMethods.WinEventProc _onForeground; // kept alive while the hook exists
    private nint _hook;
    private bool _registered;

    public WindowSwitchService(ILogger<WindowSwitchService> logger)
    {
        _logger = logger;
        _onForeground = (_, _, hwnd, _, _, _, _) => Refresh(hwnd);
        _source = new HwndSource(new HwndSourceParameters("DofusCompanionWindowSwitch") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    public bool Enabled { get; private set; }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (enabled && _hook == 0)
        {
            _hook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, 0,
                Marshal.GetFunctionPointerForDelegate(_onForeground), 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
            if (_hook == 0) _logger.LogWarning("Foreground notification could not be set up");
        }
        else if (!enabled && _hook != 0)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = 0;
        }

        Refresh(NativeMethods.GetForegroundWindow());
    }

    /// <summary>Takes TAB / Shift+TAB when a Dofus window comes to the front, gives them back otherwise.</summary>
    private void Refresh(nint foreground)
    {
        var windows = Enabled ? DofusWindows.All() : [];
        _cycle.Update(windows);
        SetKeys(Enabled && windows.Count >= 2 && windows.Contains(foreground));
    }

    private void SetKeys(bool on)
    {
        if (on == _registered) return;
        if (on)
        {
            var next = NativeMethods.RegisterHotKey(_source.Handle, NextId, NativeMethods.MOD_NOREPEAT, NativeMethods.VK_TAB);
            var previous = NativeMethods.RegisterHotKey(_source.Handle, PreviousId, NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_TAB);
            if (!next || !previous) _logger.LogWarning("TAB switch: TAB {Next}, Shift+TAB {Previous} could not be registered", next, previous);
        }
        else
        {
            NativeMethods.UnregisterHotKey(_source.Handle, NextId);
            NativeMethods.UnregisterHotKey(_source.Handle, PreviousId);
        }

        _registered = on;
    }

    private void Switch(int step)
    {
        var current = NativeMethods.GetForegroundWindow();
        _cycle.Update(DofusWindows.All());
        var target = _cycle.Next(current, step);
        if (target == 0 || target == current) return;
        if (NativeMethods.IsIconic(target)) NativeMethods.ShowWindow(target, NativeMethods.SW_RESTORE);
        // Allowed by Windows because this process has just received the key press (WM_HOTKEY).
        if (!NativeMethods.SetForegroundWindow(target)) _logger.LogWarning("TAB switch: window could not be brought to the front");
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && (int)wParam is NextId or PreviousId)
        {
            handled = true;
            Switch((int)wParam == NextId ? 1 : -1);
        }

        return 0;
    }

    public void Dispose()
    {
        SetEnabled(false);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
