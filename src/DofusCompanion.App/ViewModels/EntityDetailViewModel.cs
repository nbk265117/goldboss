using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DofusCompanion.App.Localization;
using DofusCompanion.App.Services;
using DofusCompanion.Application;
using DofusCompanion.Domain;

namespace DofusCompanion.App.ViewModels;

public sealed record RelatedEntityLine(string Id, string Name, string Relation, string TypeLabel);

/// <summary>
/// Displays one entity with all its mechanics (or an explicit "nothing documented"). Re-evaluates when the
/// fight context (turn, observed entities) or the language changes.
/// </summary>
public sealed partial class EntityDetailViewModel : ObservableObject
{
    private readonly KnowledgeService _knowledge;
    private readonly FightContext _fight;
    private readonly SourceOpener _opener;
    private CancellationTokenSource? _cts;

    private readonly EntityImageProvider _images;
    private readonly StatusService _status;
    private GameEntity? _entity;

    public EntityDetailViewModel(KnowledgeService knowledge, FightContext fight, SourceOpener opener, EntityImageProvider images, StatusService status)
    {
        _images = images;
        _status = status;
        _images.Changed += (_, id) => { if (id == EntityId) RefreshImage(); };
        _knowledge = knowledge;
        _fight = fight;
        _opener = opener;
        _fight.Changed += async (_, _) => await ReloadAsync();
        Loc.Instance.LanguageChanged += async (_, _) => await ReloadAsync();
    }

    public ObservableCollection<MechanicCard> Mechanics { get; } = [];
    public ObservableCollection<RelatedEntityLine> Related { get; } = [];

    [ObservableProperty] public partial string? EntityId { get; private set; }
    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string TypeLabel { get; private set; } = "";
    [ObservableProperty] public partial string? Locations { get; private set; }
    [ObservableProperty] public partial string? Aliases { get; private set; }
    [ObservableProperty] public partial string? Level { get; private set; }
    [ObservableProperty] public partial string? Description { get; private set; }
    [ObservableProperty] public partial string? Attributes { get; private set; }
    [ObservableProperty] public partial string EntityVerificationLabel { get; private set; } = "";
    [ObservableProperty] public partial bool HasEntity { get; private set; }
    [ObservableProperty] public partial bool NoMechanicDocumented { get; private set; }

    /// <summary>Raised when the user clicks a related entity; the host decides where to navigate.</summary>
    public event EventHandler<string>? NavigateRequested;

    public FightContext Fight => _fight;

    public async Task ShowAsync(string? entityId)
    {
        EntityId = entityId;
        await ReloadAsync();
    }

    [ObservableProperty] public partial System.Windows.Media.ImageSource? Image { get; private set; }
    [ObservableProperty] public partial bool HasImage { get; private set; }
    [ObservableProperty] public partial string Initial { get; private set; } = "?";

    private void RefreshImage()
    {
        Image = _entity is null ? null : _images.Get(_entity, EntityImageProvider.LargeSize);
        HasImage = Image is not null;
        Initial = _entity is { CanonicalName.Length: > 0 } e ? char.ToUpperInvariant(e.CanonicalName[0]).ToString() : "?";
    }

    public void Clear()
    {
        _entity = null;
        Image = null;
        HasImage = false;
        EntityId = null;
        HasEntity = false;
        Mechanics.Clear();
        Related.Clear();
    }

    [RelayCommand]
    private void Navigate(string id) => NavigateRequested?.Invoke(this, id);

    [RelayCommand]
    private void CopyCompact(MechanicCard? card)
    {
        if (card is null) return;
        try { Clipboard.SetText(card.CompactText); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy: harmless */ }
    }

    public async Task ReloadAsync()
    {
        if (EntityId is null) { Clear(); return; }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var guidance = await _knowledge.GetGuidanceAsync(EntityId, _fight.ToGuidanceContext(), ct);
        if (ct.IsCancellationRequested) return;
        if (guidance is null) { Clear(); return; }

        var e = guidance.Entity;
        _entity = e;
        RefreshImage();
        Name = e.CanonicalName;
        TypeLabel = GuidancePresenter.TypeLabel(e.Type);
        Locations = guidance.Locations.Count == 0 ? null : string.Join(", ", guidance.Locations.Select(l => l.CanonicalName));
        Aliases = e.Aliases.Count == 0 ? null : string.Join(", ", e.Aliases.Select(a => a.Text));
        Level = e.LevelText;
        Description = e.Description;
        Attributes = e.Attributes.Count == 0 ? null : string.Join(Environment.NewLine, e.Attributes.Select(kv => $"{kv.Key} : {kv.Value}"));
        EntityVerificationLabel = GuidancePresenter.BadgeLabel(e.Verification);
        NoMechanicDocumented = guidance.NoMechanicDocumented;

        Mechanics.Clear();
        foreach (var m in guidance.Mechanics)
            Mechanics.Add(GuidancePresenter.Card(m, _opener));
        foreach (var boss in guidance.ContainedBosses)
            foreach (var m in boss.Mechanics)
                Mechanics.Add(GuidancePresenter.Card(m, _opener, boss.Entity.CanonicalName));

        Related.Clear();
        foreach (var (rel, other) in await _knowledge.GetRelatedAsync(EntityId, ct))
        {
            var direction = rel.FromId == EntityId ? "Out" : "In";
            Related.Add(new RelatedEntityLine(other.Id, other.CanonicalName, Loc.Instance[$"Relation.{rel.Type}.{direction}"], GuidancePresenter.TypeLabel(other.Type)));
        }

        HasEntity = true;
    }
}
