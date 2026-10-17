using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DofusCompanion.App.Localization;
using DofusCompanion.App.Services;
using DofusCompanion.Application.Abstractions;
using DofusCompanion.Infrastructure;
using DofusCompanion.Infrastructure.Logging;
using DofusCompanion.Infrastructure.Persistence;
using Microsoft.Win32;

namespace DofusCompanion.App.ViewModels;

public sealed record DiagnosticLine(string Label, string Value, Tone Tone = Tone.Neutral);

/// <summary>Environment, OCR, database, capture and recent-error status, plus a reviewable diagnostic export.</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IOcrProvider _ocr;
    private readonly IKnowledgeRepository _repository;
    private readonly InMemoryLogSink _sink;
    private readonly HotkeyService _hotkeys;
    private readonly AppPaths _paths;
    private readonly SourceOpener _opener;
    private readonly StartupMetrics _metrics;

    public DiagnosticsViewModel(IOcrProvider ocr, IKnowledgeRepository repository, InMemoryLogSink sink, HotkeyService hotkeys,
        AppPaths paths, SourceOpener opener, StartupMetrics metrics)
    {
        _ocr = ocr;
        _repository = repository;
        _sink = sink;
        _hotkeys = hotkeys;
        _paths = paths;
        _opener = opener;
        _metrics = metrics;
        _sink.EntryAdded += (_, _) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(RefreshErrors);
    }

    public ObservableCollection<DiagnosticLine> Lines { get; } = [];
    public ObservableCollection<LogEntry> RecentErrors { get; } = [];
    [ObservableProperty] public partial string ExportPreview { get; private set; } = "";
    [ObservableProperty] public partial bool HasExportPreview { get; private set; }

    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var L = Loc.Instance;
        Lines.Clear();
        Lines.Add(new(L["Diag.Version"], AppVersion));
        Lines.Add(new(L["Diag.Runtime"], RuntimeInformation.FrameworkDescription));
        Lines.Add(new(L["Diag.Os"], $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"));
        Lines.Add(new(L["Diag.Build"], $"{(System.Diagnostics.Debugger.IsAttached ? "Debug (debugger)" : BuildConfiguration)} · {File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location):yyyy-MM-dd HH:mm}"));
        Lines.Add(new(L["Diag.Startup"], _metrics.StartupDuration is { } d ? $"{d.TotalMilliseconds:0} ms" : "—",
            _metrics.StartupDuration is { } s && s.TotalSeconds > 3 ? Tone.Caution : Tone.Neutral));

        var ocr = _ocr.GetAvailability();
        Lines.Add(new(L["Diag.OcrEngine"], ocr.IsAvailable ? $"{_ocr.Name} — OK (max {_ocr.MaxImageDimension}px)" : ocr.Problem ?? "—", ocr.IsAvailable ? Tone.Good : Tone.Bad));
        Lines.Add(new(L["Diag.OcrLanguages"], ocr.Languages.Count == 0 ? "—" : string.Join(", ", ocr.Languages), ocr.Languages.Count == 0 ? Tone.Bad : Tone.Neutral));

        try
        {
            var db = await _repository.GetStatusAsync();
            Lines.Add(new(L["Diag.Database"], db.Path, Tone.Good));
            Lines.Add(new(L["Diag.Schema"], $"{db.SchemaVersion} / {DatabaseMigrator.LatestVersion}", db.SchemaVersion == DatabaseMigrator.LatestVersion ? Tone.Good : Tone.Caution));
            Lines.Add(new(L["Diag.Content"], L.Format("Diag.ContentValue", db.EntityCount, db.MechanicCount, db.SourceCount, db.FileSizeBytes / 1024)));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Lines.Add(new(L["Diag.Database"], ex.Message, Tone.Bad));
        }

        Lines.Add(new(L["Diag.Capture"], OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? L["Diag.CaptureOk"] : L["Diag.CaptureOld"]));
        foreach (var h in _hotkeys.Registrations)
            Lines.Add(new(L.Format("Diag.Hotkey", L[$"Hotkey.{h.Name}"]), h.Success ? h.Gesture : string.IsNullOrEmpty(h.Gesture) ? L["Diag.HotkeyNone"] : $"{h.Gesture} — {L["Diag.HotkeyConflict"]} ({h.Error})",
                h.Success ? Tone.Good : string.IsNullOrEmpty(h.Gesture) ? Tone.Neutral : Tone.Bad));
        Lines.Add(new(L["Diag.DataFolder"], _paths.Root));
        RefreshErrors();
    }

    private const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private void RefreshErrors()
    {
        RecentErrors.Clear();
        foreach (var e in _sink.Snapshot().Reverse().Take(50)) RecentErrors.Add(e);
    }

    /// <summary>Builds the diagnostic text and shows it for review. Nothing is sent anywhere.</summary>
    [RelayCommand]
    private async Task PrepareExportAsync()
    {
        await RefreshAsync();
        var sb = new StringBuilder();
        sb.AppendLine("Dofus Companion — diagnostic report");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        sb.AppendLine("Contains: versions, OCR/database status, recent warnings and errors. No screenshots, no OCR text, no account data.");
        sb.AppendLine();
        foreach (var l in Lines) sb.AppendLine($"{l.Label}: {Redact(l.Value)}");
        sb.AppendLine();
        sb.AppendLine("Recent warnings/errors:");
        foreach (var e in _sink.Snapshot()) sb.AppendLine($"{e.Timestamp:O} [{e.Level}] {e.Category}: {Redact(e.Message)}");
        ExportPreview = sb.ToString();
        HasExportPreview = true;
    }

    /// <summary>Replaces the Windows user name in paths so the report can be shared.</summary>
    private static string Redact(string s) => s.Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void SaveExport()
    {
        var dlg = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = $"dofus-companion-diagnostic-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, ExportPreview, new UTF8Encoding(false));
        HasExportPreview = false;
    }

    [RelayCommand]
    private void CloseExport() => HasExportPreview = false;

    [RelayCommand]
    private void OpenLogs() => _opener.OpenFolder(_paths.Logs);
}

/// <summary>Measured startup time (process start → main window rendered), shown in Diagnostics.</summary>
public sealed class StartupMetrics
{
    public TimeSpan? StartupDuration { get; set; }
}
