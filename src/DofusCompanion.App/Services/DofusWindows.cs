using System.Diagnostics;

namespace DofusCompanion.App.Services;

/// <summary>
/// Finds the Dofus client windows. Only process NAMES and window handles are read; the game process is never
/// opened or inspected.
/// </summary>
internal static class DofusWindows
{
    private static bool IsDofusName(string name) =>
        name.StartsWith("Dofus", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("DofusGoldBoss", StringComparison.OrdinalIgnoreCase);

    /// <summary>Main windows of every running Dofus client (minimised ones included), by process id.</summary>
    public static List<nint> All()
    {
        var found = new List<(int Pid, nint Hwnd)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId || !IsDofusName(p.ProcessName)) continue;
                    var h = p.MainWindowHandle;
                    if (h != 0) found.Add((p.Id, h));
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        return found.OrderBy(f => f.Pid).Select(f => f.Hwnd).ToList();
    }

    /// <summary>The first Dofus window that is not minimised, or 0.</summary>
    public static nint FirstVisible() => All().FirstOrDefault(h => !NativeMethods.IsIconic(h));

    /// <summary>The foreground window if it belongs to the Dofus client, or 0.</summary>
    public static nint Foreground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        return IsDofus(hwnd) ? hwnd : 0;
    }

    public static bool IsDofus(nint hwnd)
    {
        if (hwnd == 0) return false;
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return IsDofusName(p.ProcessName);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
