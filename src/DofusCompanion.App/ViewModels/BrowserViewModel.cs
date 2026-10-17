using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DofusCompanion.App.Localization;
using DofusCompanion.App.Services;
using DofusCompanion.Application;
using DofusCompanion.Application.Settings;
using DofusCompanion.Domain;
using Microsoft.Extensions.Logging;

namespace DofusCompanion.App.ViewModels;

public sealed record FilterOption<T>(string Label, T? Value);

public sealed record ResultLine(SearchResult Result)
{
    public string Id => Result.Entity.Id;
    public string Name => Result.Entity.CanonicalName;
    public string TypeLabel => GuidancePresenter.TypeLabel(Result.Entity.Type);
    public string MatchedText => Result.MatchedText;
    public bool MatchedAlias => !string.Equals(Result.MatchedText, Result.Entity.CanonicalName, StringComparison.Ordinal) && Result.Kind != SearchMatchKind.MechanicText;
    public string MechanicsLabel => Result.MechanicCount == 0
        ? Loc.Instance["Browser.NoMechanic"]
        : GuidancePresenter.BadgeLabel(Result.BestMechanicVerification) is { Length: > 0 } badge
            ? Loc.Instance.Format("Browser.MechanicCount", Result.MechanicCount, badge)
            : Loc.Instance.Format("Browser.MechanicCountPlain", Result.MechanicCount);
    public Tone Tone => Result.MechanicCount == 0 ? Tone.Neutral : GuidancePresenter.VerificationTone(Result.BestMechanicVerification);
    /// <summary>Small picture for result lists; null shows the initial-letter placeholder.</summary>
    public System.Windows.Media.ImageSource? Thumbnail => EntityImageProvider.Current?.Get(Result.Entity, EntityImageProvider.ThumbnailSize);
    public bool HasThumbnail => Thumbnail is not null;
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
}

/// <summary>Entity browser: debounced search with type/location/family/mechanic filters and a detail pane.</summary>
public sealed partial class BrowserViewModel : ObservableObject
{
    private readonly KnowledgeService _knowledge;
    private readonly ISettingsStore _settings;
    private readonly StatusService _status;
    private readonly ILogger<BrowserViewModel> _logger;
    private CancellationTokenSource? _searchCts;

    public BrowserViewModel(KnowledgeService knowledge, EntityDetailViewModel detail, ISettingsStore settings, StatusService status, ILogger<BrowserViewModel> logger)
    {
        _knowledge = knowledge;
        _settings = settings;
        _status = status;
        _logger = logger;
        Detail = detail;
        Detail.NavigateRequested += async (_, id) => await OpenEntityAsync(id);
        Loc.Instance.LanguageChanged += async (_, _) => await LoadFiltersAsync();
    }

    public EntityDetailViewModel Detail { get; }
    public ObservableCollection<ResultLine> Results { get; } = [];
    public ObservableCollection<FilterOption<EntityType?>> TypeOptions { get; } = [];
    public ObservableCollection<FilterOption<string>> LocationOptions { get; } = [];
    public ObservableCollection<FilterOption<string>> FamilyOptions { get; } = [];
    public ObservableCollection<FilterOption<MechanicType?>> MechanicOptions { get; } = [];
    public ObservableCollection<string> History { get; } = [];

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial FilterOption<EntityType?>? SelectedType { get; set; }
    [ObservableProperty] public partial FilterOption<string>? SelectedLocation { get; set; }
    [ObservableProperty] public partial FilterOption<string>? SelectedFamily { get; set; }
    [ObservableProperty] public partial FilterOption<MechanicType?>? SelectedMechanic { get; set; }
    [ObservableProperty] public partial ResultLine? SelectedResult { get; set; }
    [ObservableProperty] public partial string ResultSummary { get; set; } = "";
    [ObservableProperty] public partial double LastSearchMs { get; set; }

    public async Task InitializeAsync()
    {
        await LoadFiltersAsync();
        History.Clear();
        foreach (var h in _settings.Current.SearchHistory) History.Add(h);
        await SearchAsync();
    }

    public async Task LoadFiltersAsync()
    {
        var keepType = SelectedType?.Value;
        TypeOptions.Clear();
        TypeOptions.Add(new(Loc.Instance["Filter.AllTypes"], null));
        foreach (var t in Enum.GetValues<EntityType>()) TypeOptions.Add(new(GuidancePresenter.TypeLabel(t), t));
        SelectedType = TypeOptions.FirstOrDefault(o => o.Value == keepType) ?? TypeOptions[0];

        LocationOptions.Clear();
        LocationOptions.Add(new(Loc.Instance["Filter.AllLocations"], null));
        foreach (var e in (await _knowledge.GetEntitiesAsync(EntityType.Area)).Concat(await _knowledge.GetEntitiesAsync(EntityType.Dungeon)))
            LocationOptions.Add(new($"{e.CanonicalName} ({GuidancePresenter.TypeLabel(e.Type)})", e.Id));
        SelectedLocation ??= LocationOptions[0];

        FamilyOptions.Clear();
        FamilyOptions.Add(new(Loc.Instance["Filter.AllFamilies"], null));
        foreach (var e in await _knowledge.GetEntitiesAsync(EntityType.MonsterFamily))
            FamilyOptions.Add(new(e.CanonicalName, e.Id));
        SelectedFamily ??= FamilyOptions[0];

        MechanicOptions.Clear();
        MechanicOptions.Add(new(Loc.Instance["Filter.AnyMechanic"], null));
        foreach (var t in Enum.GetValues<MechanicType>()) MechanicOptions.Add(new(Loc.Instance[$"MechanicType.{t}"], t));
        SelectedMechanic ??= MechanicOptions[0];
    }

    partial void OnQueryChanged(string value) => _ = DebouncedSearchAsync();
    partial void OnSelectedTypeChanged(FilterOption<EntityType?>? value) => _ = SearchAsync();
    partial void OnSelectedLocationChanged(FilterOption<string>? value) => _ = SearchAsync();
    partial void OnSelectedFamilyChanged(FilterOption<string>? value) => _ = SearchAsync();
    partial void OnSelectedMechanicChanged(FilterOption<MechanicType?>? value) => _ = SearchAsync();
    partial void OnSelectedResultChanged(ResultLine? value) => _ = Detail.ShowAsync(value?.Id);

    private async Task DebouncedSearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(200, cts.Token);
            await SearchAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct = default)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var filter = new SearchFilter
            {
                Types = SelectedType?.Value is { } t ? new HashSet<EntityType> { t } : null,
                LocationId = SelectedLocation?.Value,
                FamilyId = SelectedFamily?.Value,
                MechanicType = SelectedMechanic?.Value,
                MaxResults = 200,
            };
            var results = await _knowledge.SearchAsync(Query, filter, ct);
            if (ct.IsCancellationRequested) return;
            LastSearchMs = sw.Elapsed.TotalMilliseconds;

            var keep = SelectedResult?.Id;
            Results.Clear();
            foreach (var r in results) Results.Add(new ResultLine(r));
            ResultSummary = results.Count == 0 && Query.Length > 0
                ? Loc.Instance.Format("Browser.NoResult", Query)
                : Loc.Instance.Format("Browser.ResultCount", results.Count);
            SelectedResult = Results.FirstOrDefault(r => r.Id == keep);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _logger.LogError(ex, "Search failed");
            _status.Error(Loc.Instance.Format("Error.Search", ex.Message));
        }
    }

    [RelayCommand]
    private void RememberQuery()
    {
        if (!_settings.Current.SearchHistoryEnabled || string.IsNullOrWhiteSpace(Query)) return;
        var list = _settings.Current.SearchHistory;
        list.Remove(Query);
        list.Insert(0, Query);
        if (list.Count > 20) list.RemoveRange(20, list.Count - 20);
        _settings.Save();
        History.Clear();
        foreach (var h in list) History.Add(h);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        Query = "";
        SelectedType = TypeOptions.FirstOrDefault();
        SelectedLocation = LocationOptions.FirstOrDefault();
        SelectedFamily = FamilyOptions.FirstOrDefault();
        SelectedMechanic = MechanicOptions.FirstOrDefault();
    }

    /// <summary>Shows an entity in the detail pane even if it is not in the current result list.</summary>
    public async Task OpenEntityAsync(string id)
    {
        var line = Results.FirstOrDefault(r => r.Id == id);
        if (line is not null)
        {
            SelectedResult = line;
            return;
        }

        SelectedResult = null;
        await Detail.ShowAsync(id);
    }
}
