using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DofusCompanion.App.Services;

/// <summary>
/// Opens a source URL in the user's default browser. Only absolute http(s) URLs are opened; this is the
/// only outbound network action of the application and it always happens on an explicit click.
/// </summary>
public sealed class SourceOpener(ILogger<SourceOpener> logger)
{
    public event EventHandler<string>? Failed;

    public static bool IsOpenable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    public void Open(string? url)
    {
        if (!IsOpenable(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not open {Url}", url);
            Failed?.Invoke(this, url!);
        }
    }

    /// <summary>Opens a local folder in Explorer (data/logs folder from Settings and Diagnostics).</summary>
    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not open folder {Path}", path);
            Failed?.Invoke(this, path);
        }
    }
}
