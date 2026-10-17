using System.Text;
using System.Text.Json;
using DofusCompanion.Application.Settings;

namespace DofusCompanion.Infrastructure;

/// <summary>Settings persisted as JSON. Writes are atomic (temp file + move); a corrupt file is backed up, never lost.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals, // NaN = "no saved position yet"
    };

    private readonly string _path;
    private readonly Lock _lock = new();

    public JsonSettingsStore(string path)
    {
        _path = path;
        Current = Load(out var problem);
        LoadProblem = problem;
    }

    public AppSettings Current { get; }
    public string? LoadProblem { get; }
    public event EventHandler? Saved;

    private AppSettings Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path, Encoding.UTF8), Options) ?? new AppSettings();
            Migrate(loaded);
            Sanitize(loaded);
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var backup = _path + ".bak";
            try { File.Copy(_path, backup, overwrite: true); } catch (IOException) { }
            problem = $"{ex.Message} (backup: {backup})";
            return new AppSettings();
        }
    }

    /// <summary>Upgrades settings written by older versions.</summary>
    internal static void Migrate(AppSettings s)
    {
        if (s.SettingsVersion < 2) s.ScreenCaptureEnabled = true; // 0.5.1: on-demand capture allowed by default
        s.SettingsVersion = Math.Max(s.SettingsVersion, AppSettings.CurrentVersion);
    }

    /// <summary>Clamps values a hand-edited file could make dangerous (invisible overlay, absurd sizes).</summary>
    internal static void Sanitize(AppSettings s)
    {
        s.OverlayOpacity = double.IsFinite(s.OverlayOpacity) ? Math.Clamp(s.OverlayOpacity, 0.3, 1.0) : 0.92;
        s.Overlay ??= new WindowPlacement { Left = double.NaN, Top = double.NaN, Width = 380, Height = 520 };
        s.MainWindow ??= new WindowPlacement { Left = double.NaN, Top = double.NaN, Width = 1100, Height = 720 };
        s.Overlay.Width = Clamp(s.Overlay.Width, 260, 1600, 380);
        s.Overlay.Height = Clamp(s.Overlay.Height, 160, 1600, 520);
        s.MainWindow.Width = Clamp(s.MainWindow.Width, 700, 4000, 1100);
        s.MainWindow.Height = Clamp(s.MainWindow.Height, 480, 3000, 720);
        s.MatchMinScore = Clamp(s.MatchMinScore, 0.5, 0.99, 0.75);
        s.MatchAutoAcceptScore = Clamp(s.MatchAutoAcceptScore, s.MatchMinScore, 1.0, 0.92);
        s.ScreenshotRetentionDays = (int)Clamp(s.ScreenshotRetentionDays, 1, 365, 7);
        s.SearchHistory ??= [];
        if (s.SearchHistory.Count > 50) s.SearchHistory = s.SearchHistory.Take(50).ToList();
        if (s.Language is not ("fr" or "en" or "ar")) s.Language = "fr"; // ar = Moroccan Darija
        if (s.Theme is not ("Dark" or "Light")) s.Theme = "Dark";
        s.HotkeyToggleOverlay ??= "";
        s.HotkeyToggleClickThrough ??= "";
        s.HotkeyCapture ??= "";
        s.OcrLanguage ??= "fr-FR";
        s.LogLevel ??= "Information";
    }

    private static double Clamp(double v, double min, double max, double fallback) =>
        double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    public void Save()
    {
        lock (_lock)
        {
            Sanitize(Current);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options), new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }

        Saved?.Invoke(this, EventArgs.Empty);
    }
}
