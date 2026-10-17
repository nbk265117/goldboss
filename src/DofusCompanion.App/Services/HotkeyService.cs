using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace DofusCompanion.App.Services;

public sealed record HotkeyRegistration(string Name, string Gesture, bool Success, string? Error);

/// <summary>
/// System-wide shortcuts via RegisterHotKey on a hidden message window. These only control the companion
/// (show/hide, click-through, capture); they never send anything to the game. A shortcut already taken by
/// another program is reported to the user instead of failing silently.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly ILogger<HotkeyService> _logger;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = [];
    private int _nextId = 0x0D00;

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
        _source = new HwndSource(new HwndSourceParameters("DofusCompanionHotkeys") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    public IReadOnlyList<HotkeyRegistration> Registrations => _registrations;
    private readonly List<HotkeyRegistration> _registrations = [];

    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys)
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
        _registrations.Clear();
    }

    public HotkeyRegistration Register(string name, string gesture, Action handler)
    {
        HotkeyRegistration result;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            result = new HotkeyRegistration(name, gesture, false, null);
        }
        else if (!TryParse(gesture, out var modifiers, out var vk, out var parseError))
        {
            result = new HotkeyRegistration(name, gesture, false, parseError);
        }
        else
        {
            var id = _nextId++;
            if (NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, vk))
            {
                _handlers[id] = handler;
                result = new HotkeyRegistration(name, gesture, true, null);
            }
            else
            {
                // ERROR_HOTKEY_ALREADY_REGISTERED (1409) is the common case.
                result = new HotkeyRegistration(name, gesture, false, $"Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                _logger.LogWarning("Hotkey {Gesture} for {Name} could not be registered", gesture, name);
            }
        }

        _registrations.Add(result);
        return result;
    }

    /// <summary>Parses "Ctrl+Shift+F9". At least one modifier is required to avoid stealing plain keys from the game.</summary>
    public static bool TryParse(string gesture, out uint modifiers, out uint vk, out string? error)
    {
        modifiers = 0;
        vk = 0;
        error = null;
        Key? key = null;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "SHIFT": modifiers |= NativeMethods.MOD_SHIFT; break;
                case "ALT": modifiers |= NativeMethods.MOD_ALT; break;
                case "WIN": modifiers |= NativeMethods.MOD_WIN; break;
                default:
                    if (key is not null || !Enum.TryParse<Key>(raw, ignoreCase: true, out var parsed))
                    {
                        error = $"Invalid key '{raw}'";
                        return false;
                    }

                    key = parsed;
                    break;
            }
        }

        if (key is null) { error = "Missing key"; return false; }
        if (modifiers == 0) { error = "A modifier (Ctrl, Alt, Shift or Win) is required"; return false; }
        vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return vk != 0;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _handlers.TryGetValue((int)wParam, out var handler))
        {
            handled = true;
            handler();
        }

        return 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
