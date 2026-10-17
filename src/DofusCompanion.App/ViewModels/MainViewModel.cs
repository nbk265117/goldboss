using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DofusCompanion.App.Services;

namespace DofusCompanion.App.ViewModels;

/// <summary>Shell of the main window: one view model per tab plus the shared status line.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(BrowserViewModel browser, AnalysisViewModel analysis, KnowledgeViewModel knowledge,
        SettingsViewModel settings, DiagnosticsViewModel diagnostics, StatusService status, FightContext fight, UpdateService updates)
    {
        Updates = updates;
        Browser = browser;
        Analysis = analysis;
        Knowledge = knowledge;
        Settings = settings;
        Diagnostics = diagnostics;
        Status = status;
        Fight = fight;

        Analysis.EntityConfirmed += async (_, e) => await Browser.OpenEntityAsync(e.Id);
        Knowledge.KnowledgeChanged += async (_, _) =>
        {
            await Browser.LoadFiltersAsync();
            await Browser.SearchAsync();
            await Browser.Detail.ReloadAsync();
        };
    }

    public BrowserViewModel Browser { get; }
    public AnalysisViewModel Analysis { get; }
    public KnowledgeViewModel Knowledge { get; }
    public SettingsViewModel Settings { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public StatusService Status { get; }
    public FightContext Fight { get; }
    public UpdateService Updates { get; }

    /// <summary>0 browser, 1 analysis, 2 knowledge, 3 settings, 4 diagnostics.</summary>
    [ObservableProperty] public partial int SelectedTab { get; set; }

    /// <summary>Raised to toggle the overlay window (handled by the shell).</summary>
    public event EventHandler? ToggleOverlayRequested;

    [RelayCommand]
    private void ToggleOverlay() => ToggleOverlayRequested?.Invoke(this, EventArgs.Empty);

    public void SelectTab(string name) => SelectedTab = name switch
    {
        "analysis" => 1,
        "knowledge" => 2,
        "settings" => 3,
        "diagnostics" => 4,
        _ => 0,
    };

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 2) _ = Knowledge.RefreshChoicesAsync();
        if (value == 4) _ = Diagnostics.RefreshAsync();
    }

    [RelayCommand]
    private async Task ExportSelectedAsync()
    {
        if (Browser.Detail.EntityId is { } id) await Knowledge.ExportAsync([id]);
    }

    [RelayCommand]
    private async Task EditSelectedAsync()
    {
        if (Browser.SelectedResult is { } line)
        {
            SelectedTab = 2;
            await Knowledge.EditEntityAsync(line);
        }
    }
}
