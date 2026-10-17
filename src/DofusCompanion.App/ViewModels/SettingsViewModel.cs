using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DofusCompanion.App.Localization;
using DofusCompanion.App.Services;
using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Settings;
using DofusCompanion.Infrastructure;
using Microsoft.Win32;

namespace DofusCompanion.App.ViewModels;

/// <summary>Editable copy of <see cref="AppSettings"/>; changes apply on Save (language and theme preview live).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IOcrProvider _ocr;
    private readonly IKnowledgeRepository _repository;
    private readonly StatusService _status;
    private readonly SourceOpener _opener;
    private readonly AppPaths _paths;

    public SettingsViewModel(ISettingsStore store, IOcrProvider ocr, IKnowledgeRepository repository, StatusService status, SourceOpener opener, AppPaths paths, UpdateService updates)
    {
        Updates = updates;
        _store = store;
        _ocr = ocr;
        _repository = repository;
        _status = status;
        _opener = opener;
        _paths = paths;
        Load();
    }

    public UpdateService Updates { get; }

    /// <summary>Raised after Save so the shell can re-apply theme, hotkeys, overlay and matching options.</summary>
    public event EventHandler? Applied;

    public ObservableCollection<FilterOption<string>> Languages { get; } = [new("Français", "fr"), new("English", "en"), new("الدارجة المغربية", "ar")];
    public ObservableCollection<FilterOption<string>> Themes { get; } = [];
    public ObservableCollection<string> OcrLanguages { get; } = [];
    public ObservableCollection<string> LogLevels { get; } = ["Debug", "Information", "Warning", "Error"];

    [ObservableProperty] public partial FilterOption<string>? Language { get; set; }
    [ObservableProperty] public partial FilterOption<string>? Theme { get; set; }
    [ObservableProperty] public partial bool StartWithOverlay { get; set; }
    [ObservableProperty] public partial bool CheckForUpdates { get; set; }
    [ObservableProperty] public partial bool StartMinimized { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; }
    [ObservableProperty] public partial double OverlayOpacity { get; set; }
    [ObservableProperty] public partial double OverlayWidth { get; set; }
    [ObservableProperty] public partial double OverlayHeight { get; set; }
    [ObservableProperty] public partial bool OverlayLocked { get; set; }
    [ObservableProperty] public partial bool OverlayNoActivate { get; set; }
    [ObservableProperty] public partial string HotkeyToggleOverlay { get; set; } = "";
    [ObservableProperty] public partial string HotkeyToggleClickThrough { get; set; } = "";
    [ObservableProperty] public partial string HotkeyCapture { get; set; } = "";
    [ObservableProperty] public partial string HotkeyDetect { get; set; } = "";
    [ObservableProperty] public partial string? OcrLanguage { get; set; }
    [ObservableProperty] public partial bool OcrUseVariants { get; set; }
    [ObservableProperty] public partial double MatchMinScore { get; set; }
    [ObservableProperty] public partial double MatchAutoAcceptScore { get; set; }
    [ObservableProperty] public partial bool ScreenCaptureEnabled { get; set; }
    [ObservableProperty] public partial bool ExcludeOverlayFromCapture { get; set; }
    [ObservableProperty] public partial string AutoDetectRegionText { get; private set; } = "";
    [ObservableProperty] public partial bool SaveObservations { get; set; }
    [ObservableProperty] public partial bool KeepScreenshots { get; set; }
    [ObservableProperty] public partial int ScreenshotRetentionDays { get; set; }
    [ObservableProperty] public partial bool LearnFromCorrections { get; set; }
    [ObservableProperty] public partial bool SearchHistoryEnabled { get; set; }
    [ObservableProperty] public partial string DatabasePath { get; set; } = "";
    [ObservableProperty] public partial string? LogLevel { get; set; }
    [ObservableProperty] public partial string Message { get; private set; } = "";

    public string DataFolder => _paths.Root;
    public string OcrStatus { get; private set; } = "";

    public void Load()
    {
        var s = _store.Current;
        Themes.Clear();
        Themes.Add(new(Loc.Instance["Theme.Dark"], "Dark"));
        Themes.Add(new(Loc.Instance["Theme.Light"], "Light"));
        Language = Languages.FirstOrDefault(l => l.Value == s.Language) ?? Languages[0];
        Theme = Themes.FirstOrDefault(t => t.Value == s.Theme) ?? Themes[0];
        StartWithOverlay = s.StartWithOverlay;
        CheckForUpdates = s.CheckForUpdates;
        StartMinimized = s.StartMinimized;
        CloseToTray = s.CloseToTray;
        OverlayOpacity = s.OverlayOpacity;
        OverlayWidth = s.Overlay.Width;
        OverlayHeight = s.Overlay.Height;
        OverlayLocked = s.OverlayLocked;
        OverlayNoActivate = s.OverlayNoActivate;
        HotkeyToggleOverlay = s.HotkeyToggleOverlay;
        HotkeyToggleClickThrough = s.HotkeyToggleClickThrough;
        HotkeyCapture = s.HotkeyCapture;
        HotkeyDetect = s.HotkeyDetect;
        OcrUseVariants = s.OcrUseVariants;
        MatchMinScore = s.MatchMinScore;
        MatchAutoAcceptScore = s.MatchAutoAcceptScore;
        ScreenCaptureEnabled = s.ScreenCaptureEnabled;
        ExcludeOverlayFromCapture = s.ExcludeOverlayFromCapture;
        AutoDetectRegionText = s.AutoDetectRegion is { } r ? Loc.Instance.Format("Analysis.Region", r.X, r.Y, r.Width, r.Height) : Loc.Instance["AutoDetect.WholeScreen"];
        SaveObservations = s.SaveObservations;
        KeepScreenshots = s.KeepScreenshots;
        ScreenshotRetentionDays = s.ScreenshotRetentionDays;
        LearnFromCorrections = s.LearnFromCorrections;
        SearchHistoryEnabled = s.SearchHistoryEnabled;
        DatabasePath = s.DatabasePath ?? "";
        LogLevel = s.LogLevel;

        var availability = _ocr.GetAvailability();
        OcrLanguages.Clear();
        foreach (var l in availability.Languages) OcrLanguages.Add(l);
        if (!OcrLanguages.Contains(s.OcrLanguage)) OcrLanguages.Add(s.OcrLanguage);
        OcrLanguage = s.OcrLanguage;
        OcrStatus = availability.IsAvailable
            ? Loc.Instance.Format("Settings.OcrAvailable", string.Join(", ", availability.Languages))
            : Loc.Instance.Format("Settings.OcrUnavailable", availability.Problem);
        OnPropertyChanged(nameof(OcrStatus));
        Message = "";
    }

    partial void OnLanguageChanged(FilterOption<string>? value)
    {
        if (value?.Value is { } lang && lang != Loc.Instance.Language) Loc.Instance.SetLanguage(lang);
    }

    partial void OnThemeChanged(FilterOption<string>? value)
    {
        if (value?.Value is { } theme) ThemeManager.Apply(theme);
    }

    partial void OnKeepScreenshotsChanged(bool value)
    {
        if (value) SaveObservations = true; // screenshots are only kept alongside an observation
    }

    partial void OnSaveObservationsChanged(bool value)
    {
        if (!value) KeepScreenshots = false;
    }

    [RelayCommand]
    private void Save()
    {
        foreach (var hk in new[] { HotkeyToggleOverlay, HotkeyToggleClickThrough, HotkeyCapture, HotkeyDetect })
        {
            if (!string.IsNullOrWhiteSpace(hk) && !HotkeyService.TryParse(hk, out _, out _, out var err))
            {
                Message = Loc.Instance.Format("Settings.BadHotkey", hk, err);
                return;
            }
        }

        string? db = null;
        if (!string.IsNullOrWhiteSpace(DatabasePath))
        {
            try { db = _paths.ResolveDatabase(DatabasePath); }
            catch (ArgumentException) { Message = Loc.Instance["Settings.BadDatabasePath"]; return; }
        }

        var s = _store.Current;
        var dbChanged = !string.Equals(s.DatabasePath ?? "", db ?? "", StringComparison.OrdinalIgnoreCase);
        s.Language = Language?.Value ?? "fr";
        s.Theme = Theme?.Value ?? "Dark";
        s.StartWithOverlay = StartWithOverlay;
        s.CheckForUpdates = CheckForUpdates;
        s.StartMinimized = StartMinimized;
        s.CloseToTray = CloseToTray;
        s.OverlayOpacity = OverlayOpacity;
        s.Overlay.Width = OverlayWidth;
        s.Overlay.Height = OverlayHeight;
        s.OverlayLocked = OverlayLocked;
        s.OverlayNoActivate = OverlayNoActivate;
        s.HotkeyToggleOverlay = HotkeyToggleOverlay.Trim();
        s.HotkeyToggleClickThrough = HotkeyToggleClickThrough.Trim();
        s.HotkeyCapture = HotkeyCapture.Trim();
        s.HotkeyDetect = HotkeyDetect.Trim();
        s.OcrLanguage = OcrLanguage ?? "fr-FR";
        s.OcrUseVariants = OcrUseVariants;
        s.MatchMinScore = MatchMinScore;
        s.MatchAutoAcceptScore = MatchAutoAcceptScore;
        s.ScreenCaptureEnabled = ScreenCaptureEnabled;
        s.ExcludeOverlayFromCapture = ExcludeOverlayFromCapture;
        s.SaveObservations = SaveObservations;
        s.KeepScreenshots = KeepScreenshots;
        s.ScreenshotRetentionDays = ScreenshotRetentionDays;
        s.LearnFromCorrections = LearnFromCorrections;
        s.SearchHistoryEnabled = SearchHistoryEnabled;
        if (!SearchHistoryEnabled) s.SearchHistory.Clear();
        s.DatabasePath = db;
        s.LogLevel = LogLevel ?? "Information";

        try
        {
            _store.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = Loc.Instance.Format("Settings.SaveFailed", ex.Message);
            _status.Error(Message);
            return;
        }

        Load(); // reflect sanitized values
        Message = dbChanged ? Loc.Instance["Settings.SavedRestart"] : Loc.Instance["Settings.Saved"];
        _status.Info(Message);
        Applied?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Revert()
    {
        Load();
        Loc.Instance.SetLanguage(_store.Current.Language);
        ThemeManager.Apply(_store.Current.Theme);
    }

    [RelayCommand]
    private void ClearAutoDetectRegion()
    {
        _store.Current.AutoDetectRegion = null;
        _store.Save();
        Load();
    }

    [RelayCommand]
    private void BrowseDatabase()
    {
        var dlg = new SaveFileDialog { Filter = "SQLite (*.db)|*.db", FileName = "knowledge.db", OverwritePrompt = false };
        if (dlg.ShowDialog() == true) DatabasePath = dlg.FileName;
    }

    [RelayCommand]
    private void OpenDataFolder() => _opener.OpenFolder(_paths.Root);

    [RelayCommand]
    private async Task DeleteObservationsAsync()
    {
        var n = await _repository.DeleteObservationsAsync();
        var files = 0;
        if (Directory.Exists(_paths.Screenshots))
        {
            foreach (var f in Directory.GetFiles(_paths.Screenshots, "*.png"))
            {
                try { File.Delete(f); files++; }
                catch (IOException) { }
            }
        }

        _store.Current.SearchHistory.Clear();
        _store.Save();
        Message = Loc.Instance.Format("Settings.PrivacyCleared", n, files);
        _status.Info(Message);
    }
}
