using System.Text.Json;
using DofusCompanion.Application.Matching;
using DofusCompanion.Domain;

namespace DofusCompanion.Application.Knowledge;

public enum RecordKind { GameVersion, Source, Entity, Mechanic }

public enum ChangeKind
{
    Add,
    /// <summary>Replaces an existing unverified, non-user record with equal-or-better provenance.</summary>
    Update,
    Unchanged,
    /// <summary>Needs a decision; the default resolution is always the safe one (keep existing / merge).</summary>
    Conflict,
    /// <summary>Cannot be imported (dangling reference...). See Reason.</summary>
    Skip,
}

public enum ConflictReason
{
    None,
    /// <summary>The existing record is VERIFIED and the incoming one differs.</summary>
    ExistingVerified,
    /// <summary>The existing record was created or edited by the user.</summary>
    ExistingUserEdited,
    /// <summary>The incoming record has weaker provenance than the existing one.</summary>
    WeakerProvenance,
    /// <summary>Same type and name as an existing entity with another id.</summary>
    PossibleDuplicate,
}

public enum Resolution
{
    KeepExisting,
    TakeIncoming,
    /// <summary>Entities only: add incoming aliases, attributes and relations to the existing record.</summary>
    MergeIntoExisting,
}

public sealed class PlannedChange
{
    public required RecordKind Kind { get; init; }
    public required ChangeKind Change { get; init; }
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public ConflictReason Conflict { get; init; }
    public string? Reason { get; init; }
    /// <summary>Incoming record with ids already remapped (GameVersion, KnowledgeSource, PackageEntity or Mechanic).</summary>
    public object? Incoming { get; init; }
    public object? Existing { get; init; }
    /// <summary>Precomputed merge result for entity conflicts.</summary>
    public PackageEntity? Merged { get; init; }
    public Resolution Resolution { get; set; } = Resolution.KeepExisting;
    public IReadOnlyList<Resolution> AllowedResolutions { get; init; } = [];

    /// <summary>The record that will be written, or null if nothing is written.</summary>
    public object? ToWrite => Change switch
    {
        ChangeKind.Add or ChangeKind.Update => Incoming,
        ChangeKind.Conflict => Resolution switch
        {
            Resolution.TakeIncoming => Incoming,
            Resolution.MergeIntoExisting => Merged,
            _ => null,
        },
        _ => null,
    };
}

public sealed record ExistingKnowledge(
    IReadOnlyList<GameVersion> GameVersions,
    IReadOnlyList<KnowledgeSource> Sources,
    IReadOnlyList<GameEntity> Entities,
    IReadOnlyList<EntityRelation> Relations,
    IReadOnlyList<Mechanic> Mechanics);

public sealed record ImportPlan(KnowledgePackage Package, IReadOnlyList<PlannedChange> Changes, IReadOnlyList<string> Warnings)
{
    /// <summary>SHA-256 of the package file, recorded in the import log (lets seeds re-apply only when changed).</summary>
    public string? ContentHash { get; init; }

    public int Count(ChangeKind kind) => Changes.Count(c => c.Change == kind);
}

/// <summary>
/// Computes what importing a package would do, without writing anything. Pure and deterministic, so the UI
/// can preview it and tests can cover every rule.
/// </summary>
public static class ImportPlanner
{
    private static readonly JsonSerializerOptions CompareOptions = new() { WriteIndented = false };

    public static ImportPlan Plan(KnowledgePackage package, ExistingKnowledge existing, RecordOrigin origin = RecordOrigin.Import)
    {
        if (package.TestOnly)
            throw new InvalidOperationException("Test-only packages cannot be imported into a knowledge base.");

        var changes = new List<PlannedChange>();
        var warnings = new List<string>();

        var existingEntities = existing.Entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var existingSources = existing.Sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var existingMechanics = existing.Mechanics.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var existingVersions = existing.GameVersions.ToDictionary(v => v.Id, StringComparer.Ordinal);
        var byName = existing.Entities
            .GroupBy(e => (Type: NameGroup(e.Type), TextNormalizer.Normalize(e.CanonicalName)))
            .ToDictionary(g => g.Key, g => g.First());

        // 1. Entities first, to build the id remapping used by relations and mechanics.
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicates = new List<(PackageEntity Incoming, GameEntity Existing)>();
        foreach (var pe in package.Entities)
        {
            if (!existingEntities.ContainsKey(pe.Entity.Id)
                && byName.TryGetValue((NameGroup(pe.Entity.Type), TextNormalizer.Normalize(pe.Entity.CanonicalName)), out var same))
            {
                idMap[pe.Entity.Id] = same.Id;
                duplicates.Add((pe, same));
            }
        }

        string Map(string id) => idMap.TryGetValue(id, out var mapped) ? mapped : id;

        var knownEntityIds = new HashSet<string>(existingEntities.Keys, StringComparer.Ordinal);
        foreach (var pe in package.Entities) knownEntityIds.Add(Map(pe.Entity.Id));
        var knownSourceIds = new HashSet<string>(existingSources.Keys.Concat(package.Sources.Select(s => s.Id)), StringComparer.Ordinal);

        foreach (var v in package.GameVersions)
            changes.Add(PlanSimple(RecordKind.GameVersion, v.Id, v.Version ?? v.Id, v, existingVersions.GetValueOrDefault(v.Id),
                VerificationStatus.Verified, null, RecordOrigin.Import));

        foreach (var s in package.Sources)
            changes.Add(PlanSimple(RecordKind.Source, s.Id, s.Title, s, existingSources.GetValueOrDefault(s.Id),
                VerificationStatus.Verified, null, RecordOrigin.Import));

        var duplicateIds = duplicates.Select(d => d.Incoming.Entity.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var pe in package.Entities)
        {
            var relations = RemapRelations(pe, Map, knownEntityIds, warnings);
            var incoming = new PackageEntity(pe.Entity with { Id = Map(pe.Entity.Id), Origin = origin, Aliases = pe.Entity.Aliases.Select(a => a with { Origin = origin }).ToList() }, relations);

            if (duplicateIds.Contains(pe.Entity.Id))
            {
                var target = existingEntities[Map(pe.Entity.Id)];
                changes.Add(new PlannedChange
                {
                    Kind = RecordKind.Entity,
                    Change = ChangeKind.Conflict,
                    Conflict = ConflictReason.PossibleDuplicate,
                    Id = target.Id,
                    DisplayName = pe.Entity.CanonicalName,
                    Reason = $"'{pe.Entity.Id}' has the same type and name as existing '{target.Id}'.",
                    Incoming = incoming,
                    Existing = target,
                    Merged = Merge(target, ExistingRelations(existing, target.Id), incoming),
                    // Taking the incoming record wholesale would replace a record under another id: only merge or skip.
                    AllowedResolutions = [Resolution.MergeIntoExisting, Resolution.KeepExisting],
                    Resolution = Resolution.MergeIntoExisting,
                });
                continue;
            }

            existingEntities.TryGetValue(incoming.Entity.Id, out var current);
            if (current is not null)
            {
                // Imports only ever ADD relations: a partial package (e.g. an export of a few entities) must
                // not delete relations it simply did not include.
                var union = ExistingRelations(existing, current.Id).Union(incoming.Relations).ToList();
                // Same for attributes and aliases: an update of the bundled data must keep what another package
                // added (e.g. the DofusDB picture "image", map coordinates, names in other languages).
                var attributes = new Dictionary<string, string>(current.Attributes, StringComparer.Ordinal);
                foreach (var (k, v) in incoming.Entity.Attributes) attributes[k] = v;
                var aliases = incoming.Entity.Aliases
                    .Concat(current.Aliases.Where(a => incoming.Entity.Aliases.All(i => !string.Equals(i.Text, a.Text, StringComparison.OrdinalIgnoreCase))))
                    .ToList();
                incoming = incoming with { Relations = union, Entity = incoming.Entity with { Attributes = attributes, Aliases = aliases } };
            }

            var change = PlanSimple(RecordKind.Entity, incoming.Entity.Id, incoming.Entity.CanonicalName, incoming,
                current is null ? null : new PackageEntity(current, ExistingRelations(existing, current.Id)),
                incoming.Entity.Verification, current?.Verification, current?.Origin ?? RecordOrigin.Import);
            if (change.Change == ChangeKind.Conflict && current is not null)
            {
                change = new PlannedChange
                {
                    Kind = change.Kind,
                    Change = change.Change,
                    Conflict = change.Conflict,
                    Id = change.Id,
                    DisplayName = change.DisplayName,
                    Reason = change.Reason,
                    Incoming = change.Incoming,
                    Existing = change.Existing,
                    Merged = Merge(current, ExistingRelations(existing, current.Id), incoming),
                    AllowedResolutions = [Resolution.KeepExisting, Resolution.MergeIntoExisting, Resolution.TakeIncoming],
                    Resolution = Resolution.KeepExisting,
                };
            }

            changes.Add(change);
        }

        foreach (var m in package.Mechanics)
        {
            var mapped = m with { EntityId = Map(m.EntityId), Origin = origin };
            if (!knownEntityIds.Contains(mapped.EntityId))
            {
                changes.Add(Skip(RecordKind.Mechanic, m.Id, m.Title, $"Unknown entity '{m.EntityId}'."));
                continue;
            }

            var missing = mapped.Sources.Select(s => s.SourceId).Where(id => !knownSourceIds.Contains(id)).ToList();
            if (mapped.Sources.Count == 0 || missing.Count > 0)
            {
                changes.Add(Skip(RecordKind.Mechanic, m.Id, m.Title,
                    missing.Count > 0 ? $"Unknown source(s): {string.Join(", ", missing)}." : "A mechanic must cite at least one source."));
                continue;
            }

            existingMechanics.TryGetValue(m.Id, out var current);
            changes.Add(PlanSimple(RecordKind.Mechanic, m.Id, m.Title, mapped, current,
                mapped.Verification, current?.Verification, current?.Origin ?? RecordOrigin.Import));
        }

        return new ImportPlan(package, changes, warnings);
    }

    /// <summary>
    /// Monster and Boss share one namespace for duplicate detection: game data lists bosses (and wanted-notice
    /// targets) as monsters, while the guides classify them as bosses.
    /// </summary>
    private static EntityType NameGroup(EntityType t) => t == EntityType.Boss ? EntityType.Monster : t;

    private static IReadOnlyList<EntityRelation> ExistingRelations(ExistingKnowledge existing, string id) =>
        existing.Relations.Where(r => r.FromId == id).ToList();

    private static List<EntityRelation> RemapRelations(PackageEntity pe, Func<string, string> map,
        HashSet<string> known, List<string> warnings)
    {
        var result = new List<EntityRelation>();
        foreach (var r in pe.Relations)
        {
            var to = map(r.ToId);
            if (!known.Contains(to))
            {
                warnings.Add($"Relation {pe.Entity.Id} -{r.Type}-> {r.ToId} dropped: target entity unknown.");
                continue;
            }

            result.Add(new EntityRelation(map(pe.Entity.Id), r.Type, to));
        }

        return result;
    }

    private static PackageEntity Merge(GameEntity current, IReadOnlyList<EntityRelation> currentRelations, PackageEntity incoming)
    {
        var aliasTexts = new HashSet<string>(current.AllNames().Select(TextNormalizer.Normalize), StringComparer.Ordinal);
        var aliases = current.Aliases.ToList();
        foreach (var name in incoming.Entity.AllNames())
        {
            if (aliasTexts.Add(TextNormalizer.Normalize(name)))
                aliases.Add(new EntityAlias(name, incoming.Entity.Aliases.FirstOrDefault(a => a.Text == name)?.Locale ?? incoming.Entity.Locale, RecordOrigin.Import));
        }

        var attributes = new Dictionary<string, string>(current.Attributes, StringComparer.Ordinal);
        foreach (var (k, v) in incoming.Entity.Attributes)
            attributes.TryAdd(k, v); // never overwrite an existing value during a merge

        var relations = currentRelations.ToList();
        foreach (var r in incoming.Relations)
        {
            var rel = r with { FromId = current.Id };
            if (!relations.Contains(rel)) relations.Add(rel);
        }

        var merged = current with
        {
            Aliases = aliases,
            Attributes = attributes,
            Description = current.Description ?? incoming.Entity.Description,
            LevelText = current.LevelText ?? incoming.Entity.LevelText,
            SourceIds = current.SourceIds.Union(incoming.Entity.SourceIds, StringComparer.Ordinal).ToList(),
        };
        return new PackageEntity(merged, relations);
    }

    private static PlannedChange PlanSimple(RecordKind kind, string id, string name, object incoming, object? existing,
        VerificationStatus incomingVerification, VerificationStatus? existingVerification, RecordOrigin existingOrigin)
    {
        if (existing is null)
            return new PlannedChange { Kind = kind, Change = ChangeKind.Add, Id = id, DisplayName = name, Incoming = incoming };

        if (Canonical(existing) == Canonical(incoming))
            return new PlannedChange { Kind = kind, Change = ChangeKind.Unchanged, Id = id, DisplayName = name, Incoming = incoming, Existing = existing };

        var reason = ConflictReason.None;
        if (existingOrigin == RecordOrigin.User) reason = ConflictReason.ExistingUserEdited;
        else if (existingVerification == VerificationStatus.Verified) reason = ConflictReason.ExistingVerified;
        else if (existingVerification is { } ev && incomingVerification < ev) reason = ConflictReason.WeakerProvenance;
        // Sources and game versions carry no verification status: a differing one is treated like verified data.
        else if (kind is RecordKind.Source or RecordKind.GameVersion) reason = ConflictReason.ExistingVerified;

        if (reason == ConflictReason.None)
            return new PlannedChange { Kind = kind, Change = ChangeKind.Update, Id = id, DisplayName = name, Incoming = incoming, Existing = existing };

        return new PlannedChange
        {
            Kind = kind,
            Change = ChangeKind.Conflict,
            Conflict = reason,
            Id = id,
            DisplayName = name,
            Incoming = incoming,
            Existing = existing,
            AllowedResolutions = [Resolution.KeepExisting, Resolution.TakeIncoming],
        };
    }

    private static PlannedChange Skip(RecordKind kind, string id, string name, string reason) =>
        new() { Kind = kind, Change = ChangeKind.Skip, Id = id, DisplayName = name, Reason = reason };

    /// <summary>Structural comparison that ignores record origin (import vs seed provenance of the same content).</summary>
    private static string Canonical(object record) => record switch
    {
        GameEntity e => JsonSerializer.Serialize(e with { Origin = RecordOrigin.Import }, CompareOptions),
        PackageEntity pe => JsonSerializer.Serialize(new
        {
            Entity = pe.Entity with
            {
                Origin = RecordOrigin.Import,
                Aliases = pe.Entity.Aliases.Select(a => a with { Origin = RecordOrigin.Import }).ToList(),
                Attributes = new SortedDictionary<string, string>(pe.Entity.Attributes.ToDictionary(), StringComparer.Ordinal),
            },
            Relations = pe.Relations.OrderBy(r => r.Type).ThenBy(r => r.ToId, StringComparer.Ordinal).ToList(),
        }, CompareOptions),
        Mechanic m => JsonSerializer.Serialize(m with { Origin = RecordOrigin.Import }, CompareOptions),
        _ => JsonSerializer.Serialize(record, record.GetType(), CompareOptions),
    };
}
