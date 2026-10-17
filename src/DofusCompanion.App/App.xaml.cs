using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DofusCompanion.App.Localization;
using DofusCompanion.App.Services;
using DofusCompanion.App.ViewModels;
using DofusCompanion.App.Views;
using DofusCompanion.Application;
using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Matching;
using DofusCompanion.Application.Mechanics;
using DofusCompanion.Application.Recognition;
using DofusCompanion.Application.Settings;
using DofusCompanion.Infrastructure;
using DofusCompanion.Infrastructure.Knowledge;
using DofusCompanion.Infrastructure.Logging;
using DofusCompanion.Infrastructure.Persistence;
using DofusCompanion.Ocr.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DofusCompanion.App;

/// <summary>Composition root and application lifetime.</summary>
public partial class App : System.Windows.Application, IDisposable
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private MainWindow? _main;
    private OverlayWindow? _overlay;
    private HotkeyService? _hotkeys;
    private Mutex? _singleInstance;
    private TrayIconService? _tray;
    private bool _quitting;

    private ServiceProvider Services => _services ?? throw new InvalidOperationException("Services are not initialized yet.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance only: two instances would fight over hotkeys and the database file.
        _singleInstance = new Mutex(true, @"Local\DofusGoldBoss.SingleInstance" + (DataDirFromArgs(e.Args) is { } d ? "." + StableId(d) : ""), out var first);
        if (!first)
        {
            MessageBox.Show(Loc.Instance["Error.AlreadyRunning"], Loc.Instance["App.Name"], MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // "--data-dir <folder>": portable mode / clean-install testing. Default: %LOCALAPPDATA%\DofusGoldBoss.
        var dataDir = DataDirFromArgs(e.Args);
        var paths = new AppPaths(dataDir);
        var settings = new JsonSettingsStore(paths.Settings);
        Loc.Instance.SetLanguage(settings.Current.Language);
        ThemeManager.Apply(settings.Current.Theme);

        var sink = new InMemoryLogSink();
        var fileLogger = new FileLoggerProvider(paths.Logs, sink, ParseLevel(settings.Current.LogLevel));
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Debug).AddProvider(fileLogger));
        services.AddSingleton(paths);
        services.AddSingleton<ISettingsStore>(settings);
        services.AddSingleton(sink);
        services.AddSingleton(fileLogger);

        string databasePath;
        try
        {
            databasePath = paths.ResolveDatabase(settings.Current.DatabasePath);
        }
        catch (ArgumentException)
        {
            databasePath = paths.DefaultDatabase;
        }

        services.AddSingleton(sp => new SqliteKnowledgeRepository(databasePath, sp.GetRequiredService<ILogger<SqliteKnowledgeRepository>>()));
        services.AddSingleton<IKnowledgeRepository>(sp => sp.GetRequiredService<SqliteKnowledgeRepository>());
        services.AddSingleton(_ => new MechanicsEngine());
        services.AddSingleton<KnowledgeService>();
        services.AddSingleton<KnowledgePackageSerializer>();
        services.AddSingleton<KnowledgeImportService>();
        services.AddSingleton<IOcrProvider, WindowsOcrProvider>();
        services.AddSingleton<IImageDecoder, WindowsImageDecoder>();
        services.AddSingleton<RecognitionService>();
        services.AddSingleton<ImageInputService>();
        services.AddSingleton<ScreenCaptureService>();
        services.AddSingleton<SourceOpener>();
        services.AddSingleton<EntityImageStore>();
        services.AddSingleton<EntityImageProvider>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<FightContext>();
        services.AddSingleton<StatusService>();
        services.AddSingleton<StartupMetrics>();
        services.AddTransient<EntityDetailViewModel>(); // the browser and the overlay each get their own
        services.AddSingleton<BrowserViewModel>();
        services.AddSingleton<AnalysisViewModel>();
        services.AddSingleton<KnowledgeViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<OverlayWindow>();
        _services = services.BuildServiceProvider();
        _logger = Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Dofus Gold Boss - DGB {Version} starting", DiagnosticsViewModel.AppVersion);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        if (!InitializeDatabase(Services.GetRequiredService<SqliteKnowledgeRepository>(), databasePath))
        {
            Shutdown(1);
            return;
        }

        var status = Services.GetRequiredService<StatusService>();
        if (settings.LoadProblem is not null) status.Error(Loc.Instance.Format("Error.SettingsReset", settings.LoadProblem));

        try
        {
            var applied = await Services.GetRequiredService<KnowledgeImportService>().EnsureSeedAsync();
            if (applied > 0) _logger.LogInformation("{Count} seed package(s) applied", applied);
        }
        catch (Exception ex) when (ex is PackageValidationException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // The app still works with whatever knowledge is already in the database.
            _logger.LogError(ex, "Seed import failed");
            status.Error(Loc.Instance.Format("Error.Seed", ex.Message));
        }

        try
        {
            var dropped = await Services.GetRequiredService<KnowledgeImportService>().ApplyDroppedPackagesAsync(paths.Imports);
            if (dropped.Count > 0) status.Info(Loc.Instance.Format("Status.AutoImported", string.Join(" · ", dropped)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Automatic import failed");
        }

        var knowledge = Services.GetRequiredService<KnowledgeService>();
        knowledge.MatchingOptions = MatchingFromSettings(settings.Current);
        EntityImageProvider.Current = Services.GetRequiredService<EntityImageProvider>();

        _main = Services.GetRequiredService<MainWindow>();
        _overlay = Services.GetRequiredService<OverlayWindow>();
        var mainVm = Services.GetRequiredService<MainViewModel>();
        var overlayVm = Services.GetRequiredService<OverlayViewModel>();
        var analysis = Services.GetRequiredService<AnalysisViewModel>();
        analysis.CaptureReference = () => _overlay.IsVisible ? _overlay : _main;
        analysis.BeforeCapture = async () => settings.Current.ExcludeOverlayFromCapture ? await _overlay.ExcludeFromCaptureScopeAsync() : null;

        mainVm.ToggleOverlayRequested += (_, _) => ToggleOverlay();
        overlayVm.HideRequested += (_, _) => SetOverlayVisible(false);
        overlayVm.OpenMainRequested += (_, tab) => ShowMain(tab);
        mainVm.Settings.Applied += (_, _) => ApplySettings();
        _main.Closed += (_, _) => Shutdown();
        _main.Closing += (_, args) =>
        {
            if (_quitting || !settings.Current.CloseToTray) { _quitting = true; return; }
            // Keep running in the notification area; quit from the tray menu.
            args.Cancel = true;
            _main.Hide();
            if (!settings.Current.TrayHintShown)
            {
                _tray?.Notify(Loc.Instance["Tray.StillRunning"]);
                settings.Current.TrayHintShown = true;
                settings.Save();
            }
        };

        _tray = new TrayIconService();
        _tray.ToggleOverlay += (_, _) => ToggleOverlay();
        _tray.OpenMain += (_, _) => ShowMain("browser");
        _tray.ToggleClickThrough += (_, _) =>
        {
            overlayVm.ClickThrough = !overlayVm.ClickThrough;
            if (overlayVm.ClickThrough) SetOverlayVisible(true);
        };
        _tray.Quit += (_, _) => QuitApplication();
        _tray.Show();

        _hotkeys = Services.GetRequiredService<HotkeyService>();
        RegisterHotkeys();

        await mainVm.Browser.InitializeAsync();
        await mainVm.Knowledge.RefreshChoicesAsync();

        if (settings.Current.StartMinimized) _main.WindowState = WindowState.Minimized;
        _main.Show();
        if (settings.Current.StartWithOverlay && settings.Current.OverlayVisible) SetOverlayVisible(true);

        await Dispatcher.InvokeAsync(() =>
        {
            // Measured from process start (includes runtime load) to the first idle frame after showing windows.
            var metrics = Services.GetRequiredService<StartupMetrics>();
            using var process = Process.GetCurrentProcess();
            metrics.StartupDuration = DateTime.Now - process.StartTime;
            _logger.LogInformation("Startup completed in {Ms} ms", metrics.StartupDuration.Value.TotalMilliseconds);
        }, DispatcherPriority.ApplicationIdle);

        status.Info(Loc.Instance["Status.Ready"]);
    }

    private bool InitializeDatabase(SqliteKnowledgeRepository repository, string path)
    {
        try
        {
            repository.Initialize();
            return true;
        }
        catch (DatabaseCorruptException ex)
        {
            _logger!.LogError(ex, "Database corrupt");
            var answer = MessageBox.Show(Loc.Instance.Format("Error.DatabaseCorrupt", path), Loc.Instance["App.Name"], MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (answer != MessageBoxResult.Yes) return false;
            // Keep the damaged file for inspection, then start from a fresh database (seed is re-applied).
            SqliteKnowledgeRepository.ReleaseFileHandles();
            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, backup);
            foreach (var side in new[] { path + "-wal", path + "-shm" })
                if (File.Exists(side)) File.Delete(side);
            repository.Initialize();
            return true;
        }
        catch (DatabaseMigrationException ex)
        {
            _logger!.LogError(ex, "Database migration failed");
            MessageBox.Show(Loc.Instance.Format("Error.DatabaseMigration", ex.Message), Loc.Instance["App.Name"], MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger!.LogError(ex, "Database not accessible");
            MessageBox.Show(Loc.Instance.Format("Error.DatabaseAccess", path, ex.Message), Loc.Instance["App.Name"], MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void RegisterHotkeys()
    {
        var s = Services.GetRequiredService<ISettingsStore>().Current;
        var status = Services.GetRequiredService<StatusService>();
        var overlayVm = Services.GetRequiredService<OverlayViewModel>();
        var analysis = Services.GetRequiredService<AnalysisViewModel>();
        _hotkeys!.UnregisterAll();
        var results = new[]
        {
            _hotkeys.Register("ToggleOverlay", s.HotkeyToggleOverlay, ToggleOverlay),
            _hotkeys.Register("ToggleClickThrough", s.HotkeyToggleClickThrough, () =>
            {
                overlayVm.ClickThrough = !overlayVm.ClickThrough;
                if (overlayVm.ClickThrough) SetOverlayVisible(true);
            }),
            // Not taking a key combination away from other programs for a feature that is switched off.
            _hotkeys.Register("Capture", s.ScreenCaptureEnabled ? s.HotkeyCapture : "", () => _ = analysis.CaptureAsync()),
        };
        foreach (var failed in results.Where(r => !r.Success && !string.IsNullOrEmpty(r.Gesture)))
            status.Error(Loc.Instance.Format("Error.HotkeyConflict", failed.Gesture, Loc.Instance[$"Hotkey.{failed.Name}"]));
    }

    private void ApplySettings()
    {
        var settings = Services.GetRequiredService<ISettingsStore>().Current;
        Services.GetRequiredService<FileLoggerProvider>().MinimumLevel = ParseLevel(settings.LogLevel);
        var knowledge = Services.GetRequiredService<KnowledgeService>();
        knowledge.MatchingOptions = MatchingFromSettings(settings);
        knowledge.Invalidate();
        Services.GetRequiredService<AnalysisViewModel>().RefreshCaptureAvailability();
        _overlay?.ApplySettings();
        RegisterHotkeys();
    }

    private static MatchingOptions MatchingFromSettings(AppSettings s) =>
        new() { MinCandidateScore = s.MatchMinScore, AutoAcceptScore = s.MatchAutoAcceptScore };

    private void ToggleOverlay() => SetOverlayVisible(_overlay is { IsVisible: false });

    private void SetOverlayVisible(bool visible)
    {
        if (_overlay is null) return;
        var sw = Stopwatch.StartNew();
        var settings = Services.GetRequiredService<ISettingsStore>();
        if (visible)
        {
            _overlay.Show(); // ShowActivated=False: does not steal focus from the game
        }
        else
        {
            Services.GetRequiredService<OverlayViewModel>().ClickThrough = false;
            _overlay.Hide();
        }

        _logger?.LogInformation("Overlay {State} in {Ms:0.0} ms", visible ? "shown" : "hidden", sw.Elapsed.TotalMilliseconds);
        settings.Current.OverlayVisible = visible;
        settings.Save();
    }

    private void QuitApplication()
    {
        _quitting = true;
        _main?.Close(); // saves window placement, then Closed → Shutdown
        if (_main is null) Shutdown();
    }

    private void ShowMain(string tab)
    {
        if (_main is null) return;
        Services.GetRequiredService<MainViewModel>().SelectTab(tab);
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Show();
        _main.Activate();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Programming defect: log it, show it, keep the app alive when possible.
        _logger?.LogCritical(e.Exception, "Unhandled UI exception");
        _services?.GetService<StatusService>()?.Error(Loc.Instance.Format("Error.Unexpected", e.Exception.Message));
        e.Handled = true;
    }

    /// <summary>Process-independent id (string.GetHashCode is randomized per process).</summary>
    private static string StableId(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s.ToUpperInvariant())))[..16];

    internal static string? DataDirFromArgs(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[i + 1]);
        return null;
    }

    private static LogLevel ParseLevel(string level) => Enum.TryParse<LogLevel>(level, out var l) ? l : LogLevel.Information;

    public void Dispose()
    {
        _hotkeys?.Dispose();
        _services?.Dispose();
        _singleInstance?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _services?.Dispose();
        SqliteKnowledgeRepository.ReleaseFileHandles();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
