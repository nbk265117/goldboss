namespace DofusCompanion.Application.Settings;

/// <summary>All persisted user preferences. Defaults are privacy-preserving (nothing kept, capture off).</summary>
public sealed class AppSettings
{
    public int SettingsVersion { get; set; } = 1;

    // ---- interface
    public string Language { get; set; } = "fr";
    /// <summary>"Dark" or "Light".</summary>
    public string Theme { get; set; } = "Dark";
    public bool StartWithOverlay { get; set; } = true;
    public bool StartMinimized { get; set; }
    /// <summary>Closing the main window keeps the app running in the notification area (quit from the tray menu).</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Whether the "still running in the notification area" bubble was already shown once.</summary>
    public bool TrayHintShown { get; set; }
    public WindowPlacement MainWindow { get; set; } = new() { Left = double.NaN, Top = double.NaN, Width = 1100, Height = 720 };

    // ---- overlay
    public WindowPlacement Overlay { get; set; } = new() { Left = double.NaN, Top = double.NaN, Width = 380, Height = 520 };
    public double OverlayOpacity { get; set; } = 0.92;
    public bool OverlayCompact { get; set; } = true;
    public bool OverlayLocked { get; set; }
    public bool OverlayVisible { get; set; } = true;
    /// <summary>WS_EX_NOACTIVATE: clicking the overlay does not take focus from the game (search box then needs the main window).</summary>
    public bool OverlayNoActivate { get; set; }

    // ---- shortcuts (global hotkeys, format "Ctrl+Shift+F9"). Function keys are used because global hotkeys
    // take the combination away from every other program (Ctrl+Shift+T/S/D are common browser/editor shortcuts).
    public string HotkeyToggleOverlay { get; set; } = "Ctrl+Shift+F9";
    public string HotkeyToggleClickThrough { get; set; } = "Ctrl+Shift+F10";
    /// <summary>Only registered while screen capture is enabled.</summary>
    public string HotkeyCapture { get; set; } = "Ctrl+Shift+F11";

    // ---- recognition
    public string OcrLanguage { get; set; } = "fr-FR";
    /// <summary>When false only the unprocessed image is OCR'd (faster, less robust).</summary>
    public bool OcrUseVariants { get; set; } = true;
    public double MatchMinScore { get; set; } = 0.75;
    public double MatchAutoAcceptScore { get; set; } = 0.92;

    // ---- capture & privacy
    /// <summary>Screen capture is opt-in. Off by default; image import always works.</summary>
    public bool ScreenCaptureEnabled { get; set; }
    /// <summary>
    /// Hide the overlay from Dofus Companion's OWN captures (so its text is never OCR'd). It stays visible to
    /// Discord, OBS and other screen sharing at all times.
    /// </summary>
    public bool ExcludeOverlayFromCapture { get; set; } = true;
    /// <summary>Store recognition observations (text + candidates, no image) locally.</summary>
    public bool SaveObservations { get; set; }
    /// <summary>Store analysed screenshots locally. Requires SaveObservations.</summary>
    public bool KeepScreenshots { get; set; }
    public int ScreenshotRetentionDays { get; set; } = 7;
    /// <summary>Learn user-confirmed OCR spellings as local aliases (explicit consent).</summary>
    public bool LearnFromCorrections { get; set; }
    public bool SearchHistoryEnabled { get; set; }
    public List<string> SearchHistory { get; set; } = [];

    // ---- storage & diagnostics
    /// <summary>Null = default location under %LOCALAPPDATA%.</summary>
    public string? DatabasePath { get; set; }
    public string LogLevel { get; set; } = "Information";
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public interface ISettingsStore
{
    AppSettings Current { get; }
    /// <summary>Non-null when the settings file was unreadable and defaults were used (the bad file is kept as .bak).</summary>
    string? LoadProblem { get; }
    void Save();
    event EventHandler? Saved;
}
