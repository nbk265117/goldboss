using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DofusCompanion.App.Localization;
using DofusCompanion.App.Services;
using DofusCompanion.Application;
using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Knowledge;
using DofusCompanion.Application.Matching;
using DofusCompanion.Domain;
using DofusCompanion.Infrastructure.Knowledge;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DofusCompanion.App.ViewModels;

public sealed partial class ChangeLine(PlannedChange change) : ObservableObject
{
    public PlannedChange Change { get; } = change;
    public string KindLabel => Loc.Instance[$"RecordKind.{Change.Kind}"];
    public string ChangeLabel => Loc.Instance[$"ChangeKind.{Change.Change}"];
    public string Name => Change.DisplayName;
    public string Id => Change.Id;
    public string? Reason => Change.Change == ChangeKind.Conflict ? Loc.Instance[$"Conflict.{Change.Conflict}"] + (Change.Reason is null ? "" : " — " + Change.Reason) : Change.Reason;
    public bool IsConflict => Change.Change == ChangeKind.Conflict;
    public IReadOnlyList<FilterOption<Resolution?>> Options =>
        Change.AllowedResolutions.Select(r => new FilterOption<Resolution?>(Loc.Instance[$"Resolution.{r}"], r)).ToList();

    public FilterOption<Resolution?>? SelectedOption
    {
        get => Options.FirstOrDefault(o => o.Value == Change.Resolution);
        set
        {
            if (value?.Value is { } r) Change.Resolution = r;
            OnPropertyChanged();
        }
    }
}

/// <summary>Knowledge management: validated import with preview and conflict review, export, and manual editors.</summary>
public sealed partial class KnowledgeViewModel : ObservableObject
{
    private readonly KnowledgeImportService _import;
    private readonly KnowledgeService _knowledge;
    private readonly IKnowledgeRepository _repository;
    private readonly StatusService _status;
    private readonly ILogger<KnowledgeViewModel> _logger;
    private ImportPlan? _plan;

    public KnowledgeViewModel(KnowledgeImportService import, KnowledgeService knowledge, IKnowledgeRepository repository, StatusService status, ILogger<KnowledgeViewModel> logger)
    {
        _import = import;
        _knowledge = knowledge;
        _repository = repository;
        _status = status;
        _logger = logger;
        foreach (var t in Enum.GetValues<EntityType>()) EntityTypes.Add(new(GuidancePresenter.TypeLabel(t), t));
        foreach (var t in Enum.GetValues<MechanicType>()) MechanicTypes.Add(new(Loc.Instance[$"MechanicType.{t}"], t));
        foreach (var v in Enum.GetValues<VerificationStatus>().Reverse()) Verifications.Add(new(GuidancePresenter.VerificationLabel(v), v));
        foreach (var r in Enum.GetValues<SourceReliability>().Reverse()) Reliabilities.Add(new(Loc.Instance[$"Reliability.{r}"], r));
        ResetEntityForm();
        ResetMechanicForm();
        ResetSourceForm();
    }

    /// <summary>Raised after any write so other views refresh their filters and results.</summary>
    public event EventHandler? KnowledgeChanged;

    // ------------------------------------------------------------------ import / export

    public ObservableCollection<ChangeLine> PreviewChanges { get; } = [];
    public ObservableCollection<string> PreviewWarnings { get; } = [];
    [ObservableProperty] public partial string PreviewSummary { get; private set; } = "";
    [ObservableProperty] public partial bool HasPreview { get; private set; }
    [ObservableProperty] public partial string ValidationErrors { get; private set; } = "";
    [ObservableProperty] public partial bool ShowUnchanged { get; set; }

    partial void OnShowUnchangedChanged(bool value) => FillPreview();

    [RelayCommand]
    private async Task ChooseImportAsync()
    {
        var dlg = new OpenFileDialog { Filter = Loc.Instance["Knowledge.PackageFilter"] + "|*.json", CheckFileExists = true };
        if (dlg.ShowDialog() != true) return;
        await PreviewAsync(dlg.FileName);
    }

    public async Task PreviewAsync(string path)
    {
        ValidationErrors = "";
        HasPreview = false;
        PreviewChanges.Clear();
        try
        {
            _plan = await _import.PreviewFileAsync(path);
            FillPreview();
            HasPreview = true;
            _status.Info(Loc.Instance["Knowledge.PreviewReady"]);
        }
        catch (PackageValidationException ex)
        {
            ValidationErrors = string.Join(Environment.NewLine, ex.Errors.Take(50));
            _status.Error(Loc.Instance["Knowledge.Invalid"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ValidationErrors = ex.Message;
            _status.Error(ex.Message);
        }
    }

    private void FillPreview()
    {
        PreviewChanges.Clear();
        PreviewWarnings.Clear();
        if (_plan is null) return;
        foreach (var c in _plan.Changes.Where(c => ShowUnchanged || c.Change != ChangeKind.Unchanged)
                     .OrderBy(c => c.Change == ChangeKind.Conflict ? 0 : 1).ThenBy(c => c.Kind))
            PreviewChanges.Add(new ChangeLine(c));
        foreach (var w in _plan.Warnings) PreviewWarnings.Add(w);
        PreviewSummary = Loc.Instance.Format("Knowledge.PreviewSummary", _plan.Package.Name, _plan.Count(ChangeKind.Add), _plan.Count(ChangeKind.Update),
            _plan.Count(ChangeKind.Conflict), _plan.Count(ChangeKind.Unchanged), _plan.Count(ChangeKind.Skip));
    }

    [RelayCommand]
    private async Task ApplyImportAsync()
    {
        if (_plan is null) return;
        try
        {
            await _import.ApplyAsync(_plan);
            _status.Info(Loc.Instance.Format("Knowledge.Imported", _plan.Package.Name));
            CancelImport();
            KnowledgeChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // The import ran in a transaction: nothing was written.
            _logger.LogError(ex, "Import failed");
            _status.Error(Loc.Instance.Format("Knowledge.ImportFailed", ex.Message));
        }
    }

    [RelayCommand]
    private void CancelImport()
    {
        _plan = null;
        HasPreview = false;
        PreviewChanges.Clear();
        PreviewWarnings.Clear();
        PreviewSummary = "";
    }

    [RelayCommand]
    private async Task ExportAllAsync() => await ExportAsync(null);

    /// <summary>Exports one entity (with its mechanics, sources and related entities).</summary>
    public async Task ExportAsync(IReadOnlyCollection<string>? ids)
    {
        var dlg = new SaveFileDialog
        {
            Filter = Loc.Instance["Knowledge.PackageFilter"] + "|*.json",
            FileName = $"dofus-gold-boss-{DateTime.Now:yyyyMMdd}.json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await _import.ExportToFileAsync(ids, dlg.FileName);
            _status.Info(Loc.Instance.Format("Knowledge.Exported", dlg.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Error(Loc.Instance.Format("Knowledge.ExportFailed", ex.Message));
        }
    }

    // ------------------------------------------------------------------ editors

    public ObservableCollection<FilterOption<EntityType?>> EntityTypes { get; } = [];
    public ObservableCollection<FilterOption<MechanicType?>> MechanicTypes { get; } = [];
    public ObservableCollection<FilterOption<VerificationStatus?>> Verifications { get; } = [];
    public ObservableCollection<FilterOption<SourceReliability?>> Reliabilities { get; } = [];
    public ObservableCollection<FilterOption<string>> LocationChoices { get; } = [];
    public ObservableCollection<FilterOption<string>> SourceChoices { get; } = [];
    public ObservableCollection<ResultLine> EntityLookup { get; } = [];

    public async Task RefreshChoicesAsync()
    {
        LocationChoices.Clear();
        LocationChoices.Add(new(Loc.Instance["Editor.None"], null));
        foreach (var e in (await _knowledge.GetEntitiesAsync(Domain.EntityType.Dungeon)).Concat(await _knowledge.GetEntitiesAsync(Domain.EntityType.Area)))
            LocationChoices.Add(new($"{e.CanonicalName} ({GuidancePresenter.TypeLabel(e.Type)})", e.Id));
        SourceChoices.Clear();
        SourceChoices.Add(new(Loc.Instance["Editor.None"], null));
        foreach (var s in await _knowledge.GetSourcesAsync())
            SourceChoices.Add(new($"{s.Title} — {s.Publisher}", s.Id));
    }

    [ObservableProperty] public partial string EditorMessage { get; private set; } = "";
    [ObservableProperty] public partial bool EditorMessageIsError { get; private set; }

    // ---- entity form
    [ObservableProperty] public partial string EntityId { get; set; } = "";
    [ObservableProperty] public partial FilterOption<EntityType?>? EntityType { get; set; }
    [ObservableProperty] public partial string EntityName { get; set; } = "";
    [ObservableProperty] public partial string EntityAliases { get; set; } = "";
    [ObservableProperty] public partial string EntityDescription { get; set; } = "";
    [ObservableProperty] public partial string EntityLevel { get; set; } = "";
    [ObservableProperty] public partial FilterOption<VerificationStatus?>? EntityVerification { get; set; }
    [ObservableProperty] public partial FilterOption<string>? EntityLocation { get; set; }
    [ObservableProperty] public partial FilterOption<string>? EntitySource { get; set; }

    [ObservableProperty] public partial string LookupQuery { get; set; } = "";
    partial void OnLookupQueryChanged(string value) => _ = LookupAsync(value);

    private async Task LookupAsync(string q)
    {
        EntityLookup.Clear();
        if (q.Length < 2) return;
        foreach (var r in await _knowledge.SearchAsync(q, new SearchFilter { MaxResults = 15, IncludeMechanicText = false })) EntityLookup.Add(new ResultLine(r));
    }

    /// <summary>Loads an existing entity (and its first mechanic) into the editors.</summary>
    [RelayCommand]
    public async Task EditEntityAsync(ResultLine? line)
    {
        if (line is null) return;
        await RefreshChoicesAsync();
        var e = await _repository.GetEntityAsync(line.Id);
        if (e is null) return;
        var rels = await _repository.GetRelationsAsync(e.Id);
        EntityId = e.Id;
        EntityType = EntityTypes.First(t => t.Value == e.Type);
        EntityName = e.CanonicalName;
        EntityAliases = string.Join(Environment.NewLine, e.Aliases.Select(a => a.Text));
        EntityDescription = e.Description ?? "";
        EntityLevel = e.LevelText ?? "";
        EntityVerification = Verifications.First(v => v.Value == e.Verification);
        var loc = rels.FirstOrDefault(r => r.FromId == e.Id && r.Type is RelationType.LocatedIn or RelationType.PartOf);
        EntityLocation = LocationChoices.FirstOrDefault(l => l.Value == loc?.ToId) ?? LocationChoices[0];
        EntitySource = SourceChoices.FirstOrDefault(s => e.SourceIds.Contains(s.Value ?? "")) ?? SourceChoices[0];
        MechanicEntityId = e.Id;
        MechanicEntityName = e.CanonicalName;
        var mech = (await _repository.GetMechanicsForEntityAsync(e.Id)).FirstOrDefault();
        if (mech is not null) LoadMechanic(mech);
        else ResetMechanicForm(keepEntity: true);
    }

    [RelayCommand]
    private void NewEntity() => ResetEntityForm();

    private void ResetEntityForm()
    {
        EntityId = "";
        EntityType = EntityTypes.FirstOrDefault(t => t.Value == Domain.EntityType.Boss);
        EntityName = EntityAliases = EntityDescription = EntityLevel = "";
        EntityVerification = Verifications.FirstOrDefault(v => v.Value == VerificationStatus.Unknown);
        EntityLocation = LocationChoices.FirstOrDefault();
        EntitySource = SourceChoices.FirstOrDefault();
    }

    [RelayCommand]
    private async Task SaveEntityAsync()
    {
        if (string.IsNullOrWhiteSpace(EntityName) || EntityType?.Value is not { } type)
        {
            EditorError(Loc.Instance["Editor.NameRequired"]);
            return;
        }

        if (EntityVerification?.Value == VerificationStatus.Verified && EntitySource?.Value is null)
        {
            EditorError(Loc.Instance["Editor.VerifiedNeedsSource"]);
            return;
        }

        var id = string.IsNullOrWhiteSpace(EntityId) ? $"{type.ToString().ToLowerInvariant()}.user-{Slug(EntityName)}" : EntityId;
        var existing = await _repository.GetEntityAsync(id);
        var entity = new GameEntity
        {
            Id = id,
            Type = type,
            CanonicalName = EntityName.Trim(),
            Description = Blank(EntityDescription),
            LevelText = Blank(EntityLevel),
            Aliases = Lines(EntityAliases).Select(a => new EntityAlias(a, null, RecordOrigin.User)).ToList(),
            Attributes = existing?.Attributes ?? new Dictionary<string, string>(),
            Verification = EntityVerification?.Value ?? VerificationStatus.Unknown,
            SourceIds = EntitySource?.Value is { } s ? [s] : existing?.SourceIds ?? [],
            VersionScope = existing?.VersionScope,
            Origin = RecordOrigin.User,
        };

        // Keep relations the form does not show; replace only the location relation.
        var others = (await _repository.GetRelationsAsync(id)).Where(r => r.FromId == id && r.Type is not (RelationType.LocatedIn or RelationType.PartOf)).ToList();
        if (EntityLocation?.Value is { } loc)
            others.Add(new EntityRelation(id, type is Domain.EntityType.Dungeon or Domain.EntityType.Map ? RelationType.PartOf : RelationType.LocatedIn, loc));

        await _repository.UpsertEntityAsync(entity, others);
        EntityId = id;
        MechanicEntityId = id;
        MechanicEntityName = entity.CanonicalName;
        Saved(Loc.Instance.Format("Editor.EntitySaved", entity.CanonicalName));
    }

    // ---- mechanic form
    [ObservableProperty] public partial string MechanicId { get; set; } = "";
    [ObservableProperty] public partial string? MechanicEntityId { get; set; }
    [ObservableProperty] public partial string MechanicEntityName { get; set; } = "";
    [ObservableProperty] public partial string MechanicTitle { get; set; } = "";
    [ObservableProperty] public partial FilterOption<MechanicType?>? MechanicType { get; set; }
    [ObservableProperty] public partial FilterOption<VerificationStatus?>? MechanicVerification { get; set; }
    [ObservableProperty] public partial string MechanicSummary { get; set; } = "";
    [ObservableProperty] public partial string MechanicPrerequisites { get; set; } = "";
    [ObservableProperty] public partial string MechanicSteps { get; set; } = "";
    [ObservableProperty] public partial string MechanicExpected { get; set; } = "";
    [ObservableProperty] public partial string MechanicDuration { get; set; } = "";
    [ObservableProperty] public partial string MechanicTurns { get; set; } = "";
    [ObservableProperty] public partial string MechanicWarnings { get; set; } = "";
    [ObservableProperty] public partial string MechanicUnknowns { get; set; } = "";
    [ObservableProperty] public partial FilterOption<string>? MechanicSource { get; set; }
    [ObservableProperty] public partial string MechanicQuote { get; set; } = "";
    [ObservableProperty] public partial string MechanicVersion { get; set; } = "";

    private Mechanic? _loadedMechanic;

    private void LoadMechanic(Mechanic m)
    {
        _loadedMechanic = m;
        MechanicId = m.Id;
        MechanicTitle = m.Title;
        MechanicType = MechanicTypes.First(t => t.Value == m.Type);
        MechanicVerification = Verifications.First(v => v.Value == m.Verification);
        MechanicSummary = m.Summary ?? "";
        MechanicPrerequisites = string.Join(Environment.NewLine, m.Prerequisites.Select(p => p.Description));
        MechanicSteps = string.Join(Environment.NewLine, m.Sequences.FirstOrDefault()?.Steps.OrderBy(s => s.Order).Select(s => s.Action) ?? []);
        MechanicExpected = m.ExpectedResult ?? "";
        MechanicDuration = m.VulnerabilityDuration ?? "";
        MechanicTurns = m.TurnConditions ?? "";
        MechanicWarnings = string.Join(Environment.NewLine, m.Notes.Select(n => n.Text));
        MechanicUnknowns = string.Join(Environment.NewLine, m.UnknownAspects);
        var src = m.Sources.FirstOrDefault();
        MechanicSource = SourceChoices.FirstOrDefault(s => s.Value == src?.SourceId) ?? SourceChoices.FirstOrDefault();
        MechanicQuote = src?.Quote ?? "";
        MechanicVersion = m.VersionScope ?? "";
    }

    [RelayCommand]
    private void NewMechanic() => ResetMechanicForm(keepEntity: true);

    private void ResetMechanicForm(bool keepEntity = false)
    {
        _loadedMechanic = null;
        MechanicId = "";
        if (!keepEntity)
        {
            MechanicEntityId = null;
            MechanicEntityName = "";
        }

        MechanicTitle = MechanicSummary = MechanicPrerequisites = MechanicSteps = MechanicExpected = "";
        MechanicDuration = MechanicTurns = MechanicWarnings = MechanicUnknowns = MechanicQuote = MechanicVersion = "";
        MechanicType = MechanicTypes.FirstOrDefault(t => t.Value == Domain.MechanicType.Unlock);
        MechanicVerification = Verifications.FirstOrDefault(v => v.Value == VerificationStatus.Unknown);
        MechanicSource = SourceChoices.FirstOrDefault();
    }

    [RelayCommand]
    private async Task SaveMechanicAsync()
    {
        if (MechanicEntityId is null) { EditorError(Loc.Instance["Editor.PickEntity"]); return; }
        if (string.IsNullOrWhiteSpace(MechanicTitle)) { EditorError(Loc.Instance["Editor.TitleRequired"]); return; }
        if (MechanicSource?.Value is not { } sourceId) { EditorError(Loc.Instance["Editor.SourceRequired"]); return; }

        // Steps edited as lines replace the first sequence; other alternative sequences are preserved.
        var steps = Lines(MechanicSteps).Select((a, i) => new MechanicStep { Order = i + 1, Action = a }).ToList();
        var oldSteps = _loadedMechanic?.Sequences.FirstOrDefault()?.Steps.OrderBy(s => s.Order).ToList();
        if (oldSteps is not null && oldSteps.Select(s => s.Action).SequenceEqual(steps.Select(s => s.Action)))
            steps = oldSteps; // unchanged text: keep structured targets/conditions
        var sequences = new List<ActionSequence>();
        if (steps.Count > 0) sequences.Add(new ActionSequence { Label = _loadedMechanic?.Sequences.FirstOrDefault()?.Label, Steps = steps });
        if (_loadedMechanic is not null) sequences.AddRange(_loadedMechanic.Sequences.Skip(1));

        var prereqLines = Lines(MechanicPrerequisites);
        var oldPrereqs = _loadedMechanic?.Prerequisites ?? [];
        var prereqs = oldPrereqs.Select(p => p.Description).SequenceEqual(prereqLines)
            ? oldPrereqs
            : prereqLines.Select(p => new Condition { Kind = ConditionKind.Manual, Description = p }).ToList();

        var warnLines = Lines(MechanicWarnings);
        var oldNotes = _loadedMechanic?.Notes ?? [];
        var notes = oldNotes.Select(n => n.Text).SequenceEqual(warnLines) ? oldNotes : warnLines.Select(w => new MechanicNote(NoteKind.Warning, w)).ToList();

        var id = string.IsNullOrWhiteSpace(MechanicId) ? $"mech.user.{Slug(MechanicEntityName)}.{Slug(MechanicTitle)}" : MechanicId;
        var mechanic = new Mechanic
        {
            Id = id,
            EntityId = MechanicEntityId,
            Type = MechanicType?.Value ?? Domain.MechanicType.Other,
            Title = MechanicTitle.Trim(),
            Summary = Blank(MechanicSummary),
            Verification = MechanicVerification?.Value ?? VerificationStatus.Unknown,
            Prerequisites = prereqs,
            Sequences = sequences,
            ExpectedStateChange = _loadedMechanic?.ExpectedStateChange,
            ExpectedResult = Blank(MechanicExpected),
            VulnerabilityDuration = Blank(MechanicDuration),
            TurnConditions = Blank(MechanicTurns),
            Notes = notes,
            UnknownAspects = Lines(MechanicUnknowns),
            VersionScope = Blank(MechanicVersion),
            LastVerifiedOn = DateOnly.FromDateTime(DateTime.Today),
            Sources = [new SourceReference(sourceId, Blank(MechanicQuote))],
            Origin = RecordOrigin.User,
        };
        await _repository.UpsertMechanicAsync(mechanic);
        MechanicId = id;
        _loadedMechanic = mechanic;
        Saved(Loc.Instance.Format("Editor.MechanicSaved", mechanic.Title));
    }

    // ---- source form
    [ObservableProperty] public partial string SourceTitle { get; set; } = "";
    [ObservableProperty] public partial string SourceUrl { get; set; } = "";
    [ObservableProperty] public partial string SourcePublisher { get; set; } = "";
    [ObservableProperty] public partial FilterOption<SourceReliability?>? SourceReliability { get; set; }
    [ObservableProperty] public partial string SourceUpdatedOn { get; set; } = "";

    private void ResetSourceForm()
    {
        SourceTitle = SourceUrl = SourcePublisher = SourceUpdatedOn = "";
        SourceReliability = Reliabilities.FirstOrDefault(r => r.Value == Domain.SourceReliability.UserSubmitted);
    }

    [RelayCommand]
    private async Task SaveSourceAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceTitle)) { EditorError(Loc.Instance["Editor.TitleRequired"]); return; }
        if (!string.IsNullOrWhiteSpace(SourceUrl) && !SourceOpener.IsOpenable(SourceUrl)) { EditorError(Loc.Instance["Editor.BadUrl"]); return; }
        DateOnly? updated = null;
        if (!string.IsNullOrWhiteSpace(SourceUpdatedOn))
        {
            if (!DateOnly.TryParse(SourceUpdatedOn, CultureInfo.CurrentCulture, out var d)) { EditorError(Loc.Instance["Editor.BadDate"]); return; }
            updated = d;
        }

        var source = new KnowledgeSource
        {
            Id = $"src.user.{Slug(SourceTitle)}",
            Title = SourceTitle.Trim(),
            Url = Blank(SourceUrl),
            Publisher = Blank(SourcePublisher),
            Category = SourceCategory.User,
            Reliability = SourceReliability?.Value ?? Domain.SourceReliability.UserSubmitted,
            ContentUpdatedOn = updated,
            RetrievedOn = DateOnly.FromDateTime(DateTime.Today),
            LastCheckedOn = DateOnly.FromDateTime(DateTime.Today),
        };
        await _repository.UpsertSourceAsync(source);
        Saved(Loc.Instance.Format("Editor.SourceSaved", source.Title));
        await RefreshChoicesAsync();
        MechanicSource = SourceChoices.FirstOrDefault(s => s.Value == source.Id);
        EntitySource = MechanicSource;
        ResetSourceForm();
    }

    private void Saved(string message)
    {
        _knowledge.Invalidate();
        EditorMessage = message;
        EditorMessageIsError = false;
        _status.Info(message);
        KnowledgeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EditorError(string message)
    {
        EditorMessage = message;
        EditorMessageIsError = true;
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static List<string> Lines(string s) =>
        s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    internal static string Slug(string s)
    {
        var n = TextNormalizer.Normalize(s).Replace(' ', '-');
        var sb = new StringBuilder();
        foreach (var c in n) if (char.IsAsciiLetterOrDigit(c) || c == '-') sb.Append(c);
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 60) slug = slug[..60];
        return slug.Length == 0 ? Guid.NewGuid().ToString("N")[..8] : slug;
    }
}
