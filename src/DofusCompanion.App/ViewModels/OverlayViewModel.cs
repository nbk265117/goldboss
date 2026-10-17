using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DofusCompanion.App.Localization;
using DofusCompanion.App.Services;
using DofusCompanion.Application;
using DofusCompanion.Application.Settings;

namespace DofusCompanion.App.ViewModels;

/// <summary>
/// Always-on-top companion panel: quick search, recognition status/candidates, and the selected entity's
/// mechanics in compact or expanded form. It only displays information; the player acts in game.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly KnowledgeService _knowledge;
    private readonly ISettingsStore _settings;
    private CancellationTokenSource? _searchCts;

    public OverlayViewModel(KnowledgeService knowledge, EntityDetailViewModel detail, AnalysisViewModel analysis, ISettingsStore settings, StatusService status, FightContext fight)
    {
        _knowledge = knowledge;
        _settings = settings;
        Detail = detail;
        Analysis = analysis;
        Status = status;
        Fight = fight;
        Compact = settings.Current.OverlayCompact;
        Locked = settings.Current.OverlayLocked;
        Opacity = settings.Current.OverlayOpacity;
        Detail.NavigateRequested += async (_, id) => await Detail.ShowAsync(id);
        Analysis.EntityConfirmed += async (_, e) => await ShowEntityAsync(e.Id);
    }

    public EntityDetailViewModel Detail { get; }
    public AnalysisViewModel Analysis { get; }
    public StatusService Status { get; }
    public FightContext Fight { get; }
    public ObservableCollection<ResultLine> Results { get; } = [];

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial bool ShowResults { get; set; }
    [ObservableProperty] public partial bool Compact { get; set; }
    [ObservableProperty] public partial bool Locked { get; set; }
    [ObservableProperty] public partial bool ClickThrough { get; set; }
    [ObservableProperty] public partial double Opacity { get; set; }
    [ObservableProperty] public partial bool ShowCandidates { get; set; }
    [ObservableProperty] public partial bool Detecting { get; set; }

    /// <summary>Set by the shell once the detection service exists.</summary>
    public AutoDetectService? AutoDetect { get; set; }

    /// <summary>"Détecter" button: reads the Dofus screen once and shows the boss / dungeon / monster / zone found.</summary>
    [RelayCommand]
    private async Task DetectAsync()
    {
        if (AutoDetect is null || Detecting) return;
        Detecting = true;
        try { await AutoDetect.DetectAsync(); }
        finally { Detecting = false; }
    }

    /// <summary>Raised to ask the host to hide the overlay window.</summary>
    public event EventHandler? HideRequested;
    /// <summary>Raised to ask the host to bring the main window forward on a given tab.</summary>
    public event EventHandler<string>? OpenMainRequested;

    partial void OnQueryChanged(string value) => _ = SearchAsync(value);

    partial void OnCompactChanged(bool value)
    {
        _settings.Current.OverlayCompact = value;
        _settings.Save();
    }

    partial void OnLockedChanged(bool value)
    {
        _settings.Current.OverlayLocked = value;
        _settings.Save();
    }

    private async Task SearchAsync(string q)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(150, cts.Token);
            Results.Clear();
            if (q.Trim().Length < 2)
            {
                ShowResults = false;
                return;
            }

            foreach (var r in await _knowledge.SearchAsync(q, new SearchFilter { MaxResults = 8 }, cts.Token))
                Results.Add(new ResultLine(r));
            ShowResults = true;
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private async Task PickAsync(ResultLine? line)
    {
        if (line is null) return;
        ShowResults = false;
        Query = "";
        await ShowEntityAsync(line.Id);
    }

    public async Task ShowEntityAsync(string id)
    {
        ShowCandidates = false;
        await Detail.ShowAsync(id);
    }

    /// <summary>Click on a monster/boss chip of the current fight.</summary>
    [RelayCommand]
    private async Task ShowObservedAsync(DofusCompanion.Domain.GameEntity? entity)
    {
        if (entity is not null) await ShowEntityAsync(entity.Id);
    }

    [RelayCommand]
    private void ToggleCompact() => Compact = !Compact;

    [RelayCommand]
    private void ToggleLock() => Locked = !Locked;

    [RelayCommand]
    private void Hide() => HideRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _knowledge.Invalidate();
        await Detail.ReloadAsync();
    }

    /// <summary>Clears the selected entity, recognition results and fight context.</summary>
    [RelayCommand]
    private void Reset()
    {
        Detail.Clear();
        Fight.Reset();
        Analysis.ClearCommand.Execute(null);
        Query = "";
        ShowCandidates = false;
        Status.Info(Loc.Instance["Overlay.ResetDone"]);
    }

    [RelayCommand]
    private void ToggleCandidates() => ShowCandidates = !ShowCandidates;

    [RelayCommand]
    private void OpenMain(string? tab) => OpenMainRequested?.Invoke(this, tab ?? "browser");

    [RelayCommand]
    private void TurnUp() => Fight.TurnNumber = (Fight.TurnNumber ?? 0) + 1;

    [RelayCommand]
    private void TurnDown() => Fight.TurnNumber = Fight.TurnNumber is > 1 ? Fight.TurnNumber - 1 : null;
}
