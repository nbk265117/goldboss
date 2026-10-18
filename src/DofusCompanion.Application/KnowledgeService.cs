using System.Diagnostics;
using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Knowledge;
using DofusCompanion.Application.Matching;
using DofusCompanion.Application.Mechanics;
using DofusCompanion.Domain;
using Microsoft.Extensions.Logging;

namespace DofusCompanion.Application;

public enum SearchMatchKind { Exact, Prefix, WordPrefix, Contains, Fuzzy, MechanicText }

public sealed record SearchFilter
{
    public IReadOnlySet<EntityType>? Types { get; init; }
    /// <summary>Only entities located in (or part of) this dungeon/area, directly or transitively.</summary>
    public string? LocationId { get; init; }
    public string? FamilyId { get; init; }
    public MechanicType? MechanicType { get; init; }
    /// <summary>Also search inside mechanic texts (states, triggers, effects).</summary>
    public bool IncludeMechanicText { get; init; } = true;
    public int MaxResults { get; init; } = 50;
}

public sealed record SearchResult(GameEntity Entity, double Score, SearchMatchKind Kind, string MatchedText, int MechanicCount, VerificationStatus BestMechanicVerification);

/// <summary>
/// Read-side facade over the knowledge base: cached search index, filters and guidance assembly.
/// Thread-safe; the cache is rebuilt after any write through <see cref="Invalidate"/>.
/// </summary>
public sealed class KnowledgeService(IKnowledgeRepository repository, MechanicsEngine engine, ILogger<KnowledgeService> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _snapshot;

    /// <summary>
    /// Optional translation of mechanic texts into the interface language (titles, summaries, steps, notes).
    /// Source quotes stay in their original language. Set by the app; null = texts as stored (French).
    /// </summary>
    public Func<Mechanic, Mechanic>? TranslateMechanic { get; set; }

    private IEnumerable<Mechanic> Localized(IEnumerable<Mechanic> mechanics) =>
        TranslateMechanic is { } t ? mechanics.Select(t) : mechanics;

    private sealed record IndexedName(GameEntity Entity, string Normalized, string[] Tokens, string Sorted, string Display, string[] Trigrams);

    private sealed record Snapshot(
        IReadOnlyDictionary<string, GameEntity> Entities,
        IReadOnlyList<IndexedName> Names,
        IReadOnlyList<EntityRelation> Relations,
        ILookup<string, Mechanic> MechanicsByEntity,
        IReadOnlyDictionary<string, string> MechanicTextByEntity,
        IReadOnlyDictionary<string, string> VariantBase,
        IReadOnlyDictionary<string, (int Count, VerificationStatus Best)> MechanicSummary,
        IReadOnlyDictionary<string, KnowledgeSource> Sources,
        GameVersion? CurrentVersion,
        EntityMatcher Matcher);

    public MatchingOptions MatchingOptions { get; set; } = new();

    public void Invalidate() => _snapshot = null;

    public void Dispose() => _gate.Dispose();

    public async Task<EntityMatcher> GetMatcherAsync(CancellationToken ct = default) => (await GetSnapshotAsync(ct).ConfigureAwait(false)).Matcher;

    public async Task<IReadOnlyList<GameEntity>> GetEntitiesAsync(EntityType type, CancellationToken ct = default)
    {
        var s = await GetSnapshotAsync(ct).ConfigureAwait(false);
        return s.Entities.Values.Where(e => e.Type == type).OrderBy(e => e.CanonicalName, StringComparer.CurrentCulture).ToList();
    }

    public async Task<GameEntity?> GetEntityAsync(string id, CancellationToken ct = default) =>
        (await GetSnapshotAsync(ct).ConfigureAwait(false)).Entities.GetValueOrDefault(id);

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, SearchFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= new SearchFilter();
        var s = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        var q = TextNormalizer.Normalize(query);
        var qSorted = SortTokens(q);
        var qTrigrams = new HashSet<string>(Trigrams(q), StringComparer.Ordinal);
        var results = new Dictionary<string, SearchResult>(StringComparer.Ordinal);
        var allowed = BuildFilter(s, filter);

        void Offer(GameEntity e, double score, SearchMatchKind kind, string text)
        {
            if (!allowed(e)) return;
            if (results.TryGetValue(e.Id, out var cur) && cur.Score >= score) return;
            var (count, best) = s.MechanicSummary.GetValueOrDefault(e.Id, (0, VerificationStatus.Unknown));
            results[e.Id] = new SearchResult(e, score, kind, text, count, best);
        }

        if (q.Length == 0)
        {
            // Empty query = browse: list everything that passes the filters.
            foreach (var e in s.Entities.Values) Offer(e, 0, SearchMatchKind.Contains, e.CanonicalName);
        }
        else
        {
            foreach (var n in s.Names)
            {
                ct.ThrowIfCancellationRequested();
                if (n.Normalized == q) Offer(n.Entity, 1.0, SearchMatchKind.Exact, n.Display);
                else if (n.Normalized.StartsWith(q, StringComparison.Ordinal)) Offer(n.Entity, 0.95, SearchMatchKind.Prefix, n.Display);
                else if (n.Tokens.Any(t => t.StartsWith(q, StringComparison.Ordinal))) Offer(n.Entity, 0.9, SearchMatchKind.WordPrefix, n.Display);
                else if (n.Normalized.Contains(q, StringComparison.Ordinal)) Offer(n.Entity, 0.85, SearchMatchKind.Contains, n.Display);
                else if (q.Length >= 4 && SharesEnoughTrigrams(n, qTrigrams))
                {
                    // Bounded distances skip hopeless names early (keeps a 5k-entity search well under 100 ms).
                    var fuzzy = Math.Max(StringSimilarity.RatioAtLeast(q, n.Normalized, 0.75), StringSimilarity.RatioAtLeast(qSorted, n.Sorted, 0.75));
                    if (n.Tokens.Length > 1)
                        foreach (var t in n.Tokens)
                            fuzzy = Math.Max(fuzzy, StringSimilarity.RatioAtLeast(q, t, 0.75) * 0.95);
                    if (fuzzy >= 0.75) Offer(n.Entity, fuzzy * 0.8, SearchMatchKind.Fuzzy, n.Display);
                }
            }

            if (filter.IncludeMechanicText && q.Length >= 3)
            {
                foreach (var (entityId, text) in s.MechanicTextByEntity)
                    if (text.Contains(q, StringComparison.Ordinal) && s.Entities.TryGetValue(entityId, out var e))
                        Offer(e, 0.5, SearchMatchKind.MechanicText, query);
            }
        }

        var list = results.Values
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.MechanicCount > 0)
            .ThenBy(r => r.Entity.CanonicalName, StringComparer.CurrentCulture)
            .Take(filter.MaxResults)
            .ToList();
        logger.LogDebug("Search '{Query}' -> {Count} results in {Ms} ms", query, list.Count, sw.Elapsed.TotalMilliseconds);
        return list;
    }

    public async Task<EntityGuidance?> GetGuidanceAsync(string entityId, GuidanceContext? context = null, CancellationToken ct = default)
    {
        var s = await GetSnapshotAsync(ct).ConfigureAwait(false);
        if (!s.Entities.TryGetValue(entityId, out var entity)) return null;

        var locations = s.Relations
            .Where(r => r.FromId == entityId && r.Type is RelationType.LocatedIn or RelationType.PartOf)
            .Select(r => s.Entities.GetValueOrDefault(r.ToId))
            .OfType<GameEntity>()
            .ToList();

        var guidance = engine.ExplainEntity(entity, Localized(s.MechanicsByEntity[entityId]).ToList(), s.Sources, locations, context, s.CurrentVersion);
        if (s.VariantBase.TryGetValue(entityId, out var baseId) && s.Entities.TryGetValue(baseId, out var baseBoss))
        {
            // "Tynril Ahuri" has no sheet of its own: show the verified mechanics of "Tynril", labelled with its name.
            var baseLocations = s.Relations.Where(r => r.FromId == baseId && r.Type is RelationType.LocatedIn or RelationType.PartOf)
                .Select(r => s.Entities.GetValueOrDefault(r.ToId)).OfType<GameEntity>().ToList();
            return guidance with
            {
                ContainedBosses = [engine.ExplainEntity(baseBoss, Localized(s.MechanicsByEntity[baseId]).ToList(), s.Sources, baseLocations, context, s.CurrentVersion)],
            };
        }

        if (entity.Type != EntityType.Dungeon) return guidance;

        // Searching a dungeon must show how to beat its bosses: mechanics are attached to the bosses.
        var bosses = s.Relations
            .Where(r => r.ToId == entityId && r.Type == RelationType.LocatedIn)
            .Select(r => s.Entities.GetValueOrDefault(r.FromId))
            .OfType<GameEntity>()
            .Where(e => e.Type == EntityType.Boss)
            .OrderBy(e => e.CanonicalName, StringComparer.CurrentCulture)
            .Select(b => engine.ExplainEntity(b, Localized(s.MechanicsByEntity[b.Id]).ToList(), s.Sources, [entity], context, s.CurrentVersion))
            .ToList();
        return guidance with { ContainedBosses = bosses };
    }

    /// <summary>
    /// Mechanic count and best verification per entity. A dungeon with no mechanic of its own counts those of its
    /// bosses, because its sheet shows them (otherwise search would say "no documented mechanic").
    /// </summary>
    private static Dictionary<string, (int Count, VerificationStatus Best)> MechanicSummaries(
        IReadOnlyList<GameEntity> entities, IReadOnlyList<EntityRelation> relations, IReadOnlyList<Mechanic> mechanics,
        IReadOnlyDictionary<string, string> variantBase)
    {
        var summary = mechanics.GroupBy(m => m.EntityId)
            .ToDictionary(g => g.Key, g => (g.Count(), g.Max(m => m.Verification)), StringComparer.Ordinal);
        foreach (var (variant, baseId) in variantBase)
            if (summary.TryGetValue(baseId, out var b)) summary[variant] = b;
        var types = entities.ToDictionary(e => e.Id, e => e.Type, StringComparer.Ordinal);
        foreach (var dungeon in relations
                     .Where(r => r.Type == RelationType.LocatedIn
                                 && types.GetValueOrDefault(r.FromId) == EntityType.Boss
                                 && types.GetValueOrDefault(r.ToId) == EntityType.Dungeon
                                 && !summary.ContainsKey(r.ToId))
                     .GroupBy(r => r.ToId))
        {
            var bossSummaries = dungeon.Select(r => r.FromId).Distinct(StringComparer.Ordinal)
                .Where(summary.ContainsKey).Select(id => summary[id]).ToList();
            if (bossSummaries.Count > 0)
                summary[dungeon.Key] = (bossSummaries.Sum(b => b.Item1), bossSummaries.Max(b => b.Item2));
        }

        return summary;
    }

    private const string ImageAttribute = "image";

    /// <summary>
    /// Picture for entities that have none, borrowed from the same thing under another record (in memory only):
    /// "Guerre (Tempête de l'Eliocalypse)" → "Guerre"; "Tynril" → its variant "Tynril Ahuri"; a dungeon → one of its
    /// bosses; "Expédition - Antre du Dragon Cochon" → the "Antre du Dragon Cochon" dungeon; a zone → the dungeon of
    /// the same name.
    /// </summary>
    internal static IReadOnlyList<GameEntity> WithImageFallbacks(IReadOnlyList<GameEntity> entities, IReadOnlyList<EntityRelation> relations)
    {
        static string? Image(GameEntity e) => e.Attributes.TryGetValue(ImageAttribute, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        static string Base(string name)
        {
            var paren = name.IndexOf(" (", StringComparison.Ordinal);
            return TextNormalizer.Normalize(paren > 0 ? name[..paren] : name);
        }

        var creatures = entities.Where(e => e.Type is EntityType.Boss or EntityType.Monster && Image(e) is not null)
            .Select(e => (Name: TextNormalizer.Normalize(e.CanonicalName), Image: Image(e)!, Boss: e.Type == EntityType.Boss))
            .OrderByDescending(c => c.Boss).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        var exact = creatures.GroupBy(c => c.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Image, StringComparer.Ordinal);
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entities.Where(e => e.Type is EntityType.Boss or EntityType.Monster && Image(e) is null))
        {
            // Its own name first, then its aliases ("Chouque" is "Le Chouque", "Les Guman" are "Ambi Guman"…).
            var names = new[] { Base(e.CanonicalName) }.Concat(e.Aliases.Select(x => Base(x.Text))).Where(n => n.Length >= 3).Distinct().ToList();
            var found = names.Select(n => exact.GetValueOrDefault(n)).FirstOrDefault(i => i is not null)
                        ?? names.Select(n => creatures.FirstOrDefault(c => c.Name.StartsWith(n + " ", StringComparison.Ordinal)).Image).FirstOrDefault(i => i is not null);
            if (found is not null) images[e.Id] = found;
        }

        string? CreatureImage(GameEntity e) => Image(e) ?? images.GetValueOrDefault(e.Id);
        var byId = entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var dungeonByName = entities.Where(e => e.Type == EntityType.Dungeon)
            .GroupBy(e => TextNormalizer.Normalize(e.CanonicalName), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        string? DungeonImage(GameEntity d) => Image(d) ?? relations
            .Where(r => r.ToId == d.Id && r.Type == RelationType.LocatedIn && byId.TryGetValue(r.FromId, out var b) && b.Type == EntityType.Boss)
            .Select(r => CreatureImage(byId[r.FromId])).FirstOrDefault(i => i is not null);
        foreach (var d in entities.Where(e => e.Type == EntityType.Dungeon && Image(e) is null))
        {
            var found = DungeonImage(d);
            if (found is null)
            {
                // "Expédition - X" / "Expédition de l'Audace - X": the dungeon X.
                var dash = d.CanonicalName.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0 && dungeonByName.TryGetValue(TextNormalizer.Normalize(d.CanonicalName[(dash + 3)..]), out var originals))
                    found = originals.Where(o => o.Id != d.Id).Select(DungeonImage).FirstOrDefault(i => i is not null);
            }

            if (found is not null) images[d.Id] = found;
        }

        // A zone named like a dungeon ("Laboratoire du Tynril") shows that dungeon's picture.
        foreach (var z in entities.Where(e => e.Type is EntityType.Area or EntityType.Map && Image(e) is null))
        {
            if (!dungeonByName.TryGetValue(TextNormalizer.Normalize(z.CanonicalName), out var same)) continue;
            var found = same.Select(d => Image(d) ?? images.GetValueOrDefault(d.Id)).FirstOrDefault(i => i is not null);
            if (found is not null) images[z.Id] = found;
        }

        if (images.Count == 0) return entities;
        return entities.Select(e => images.TryGetValue(e.Id, out var img)
            ? e with { Attributes = new Dictionary<string, string>(e.Attributes, StringComparer.Ordinal) { [ImageAttribute] = img } }
            : e).ToList();
    }

    /// <summary>
    /// Boss without mechanics whose name is a documented boss's name plus a qualifier ("Tynril Ahuri",
    /// "Grozilla Fatigué" → "Tynril", "Grozilla"): variants of the same fight, which share its mechanics.
    /// </summary>
    internal static Dictionary<string, string> VariantBases(IReadOnlyList<GameEntity> entities, IReadOnlyList<Mechanic> mechanics)
    {
        var documented = mechanics.Select(m => m.EntityId).ToHashSet(StringComparer.Ordinal);
        var bases = entities
            .Where(e => e.Type == EntityType.Boss && documented.Contains(e.Id))
            .Select(e => (e.Id, Name: TextNormalizer.Normalize(e.CanonicalName)))
            .Where(b => b.Name.Length >= 4)
            .OrderByDescending(b => b.Name.Length)
            .ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entities.Where(e => e.Type == EntityType.Boss && !documented.Contains(e.Id)))
        {
            var name = TextNormalizer.Normalize(e.CanonicalName);
            var b = bases.FirstOrDefault(b => name.StartsWith(b.Name + " ", StringComparison.Ordinal));
            if (b.Id is not null) result[e.Id] = b.Id;
        }

        return result;
    }

    /// <summary>Dungeons with a room at these map coordinates (from the "mapCoords" attribute of the DofusDB import).</summary>
    public async Task<IReadOnlyList<GameEntity>> GetDungeonsAtAsync(int x, int y, CancellationToken ct = default)
    {
        var s = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var key = $"{x},{y}";
        return s.Entities.Values
            .Where(e => e.Type == EntityType.Dungeon && e.Attributes.TryGetValue("mapCoords", out var c) && c.Split(';').Contains(key))
            .ToList();
    }

    /// <summary>Ids of the bosses located in a dungeon or area, directly or through its sub-areas/dungeons.</summary>
    public async Task<IReadOnlySet<string>> GetBossIdsInAsync(string placeId, CancellationToken ct = default)
    {
        var s = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var filter = BuildFilter(s, new SearchFilter { LocationId = placeId, Types = new HashSet<EntityType> { EntityType.Boss } });
        return s.Entities.Values.Where(filter).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Entities related to the given one (bosses of a dungeon, dungeons of an area, summons...).</summary>
    public async Task<IReadOnlyList<(EntityRelation Relation, GameEntity Other)>> GetRelatedAsync(string entityId, CancellationToken ct = default)
    {
        var s = await GetSnapshotAsync(ct).ConfigureAwait(false);
        return s.Relations
            .Where(r => r.FromId == entityId || r.ToId == entityId)
            .Select(r => (r, s.Entities.GetValueOrDefault(r.FromId == entityId ? r.ToId : r.FromId)))
            .Where(t => t.Item2 is not null)
            .Select(t => (t.r, t.Item2!))
            .ToList();
    }

    public async Task<IReadOnlyList<KnowledgeSource>> GetSourcesAsync(CancellationToken ct = default) =>
        (await GetSnapshotAsync(ct).ConfigureAwait(false)).Sources.Values.OrderBy(s => s.Title, StringComparer.CurrentCulture).ToList();

    public async Task<ExistingKnowledge> GetExistingKnowledgeAsync(CancellationToken ct = default) => new(
        await repository.GetGameVersionsAsync(ct).ConfigureAwait(false),
        await repository.GetAllSourcesAsync(ct).ConfigureAwait(false),
        await repository.GetAllEntitiesAsync(ct).ConfigureAwait(false),
        await repository.GetAllRelationsAsync(ct).ConfigureAwait(false),
        await repository.GetAllMechanicsAsync(ct).ConfigureAwait(false));

    private static Func<GameEntity, bool> BuildFilter(Snapshot s, SearchFilter f)
    {
        HashSet<string>? inLocation = null;
        if (f.LocationId is { } loc)
        {
            // Transitive closure over LocatedIn/PartOf so "area" also finds bosses of its dungeons.
            inLocation = new HashSet<string>(StringComparer.Ordinal) { loc };
            bool grew;
            do
            {
                grew = false;
                foreach (var r in s.Relations)
                    if (r.Type is RelationType.LocatedIn or RelationType.PartOf && inLocation.Contains(r.ToId) && inLocation.Add(r.FromId))
                        grew = true;
            } while (grew);
            inLocation.Remove(loc);
        }

        HashSet<string>? inFamily = f.FamilyId is { } fam
            ? s.Relations.Where(r => r.Type == RelationType.MemberOfFamily && r.ToId == fam).Select(r => r.FromId).ToHashSet(StringComparer.Ordinal)
            : null;

        return e =>
            (f.Types is null || f.Types.Count == 0 || f.Types.Contains(e.Type))
            && (inLocation is null || inLocation.Contains(e.Id))
            && (inFamily is null || inFamily.Contains(e.Id))
            && (f.MechanicType is null || s.MechanicsByEntity[e.Id].Any(m => m.Type == f.MechanicType));
    }

    private async Task<Snapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (_snapshot is { } existing) return existing;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_snapshot is { } again) return again;
            var sw = Stopwatch.StartNew();

            var relations = await repository.GetAllRelationsAsync(ct).ConfigureAwait(false);
            var entities = WithImageFallbacks(await repository.GetAllEntitiesAsync(ct).ConfigureAwait(false), relations);
            var mechanics = await repository.GetAllMechanicsAsync(ct).ConfigureAwait(false);
            var sources = await repository.GetAllSourcesAsync(ct).ConfigureAwait(false);
            var versions = await repository.GetGameVersionsAsync(ct).ConfigureAwait(false);

            var names = entities
                .SelectMany(e => e.AllNames().Distinct(StringComparer.OrdinalIgnoreCase).Select(n =>
                {
                    var norm = TextNormalizer.Normalize(n);
                    var tokens = TextNormalizer.Tokenize(norm);
                    return new IndexedName(e, norm, tokens, SortTokens(norm), n, Trigrams(norm));
                }))
                .Where(n => n.Normalized.Length > 0)
                .ToList();

            var mechText = mechanics
                .GroupBy(m => m.EntityId)
                .ToDictionary(g => g.Key, g => TextNormalizer.Normalize(string.Join(' ', g.SelectMany(MechanicTexts))), StringComparer.Ordinal);

            var variants = VariantBases(entities, mechanics);
            _snapshot = new Snapshot(
                entities.ToDictionary(e => e.Id, StringComparer.Ordinal),
                names,
                relations,
                mechanics.ToLookup(m => m.EntityId, StringComparer.Ordinal),
                mechText,
                variants,
                MechanicSummaries(entities, relations, mechanics, variants),
                sources.ToDictionary(x => x.Id, StringComparer.Ordinal),
                // The most recent dated release is the staleness baseline.
                versions.Where(v => v.ReleaseDate is not null).MaxBy(v => v.ReleaseDate),
                new EntityMatcher(entities, MatchingOptions));

            logger.LogInformation("Knowledge index built: {Entities} entities, {Names} names, {Mechanics} mechanics in {Ms} ms",
                entities.Count, names.Count, mechanics.Count, sw.ElapsedMilliseconds);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Cheap pre-filter before edit distances: a fuzzy hit (whole name or one of its words) must share at least
    /// one character trigram with the query. Typos keep most trigrams; unrelated names share none.
    /// </summary>
    private static bool SharesEnoughTrigrams(IndexedName n, HashSet<string> q)
    {
        foreach (var t in n.Trigrams)
            if (q.Contains(t)) return true;
        return false;
    }

    private static string[] Trigrams(string normalized)
    {
        if (normalized.Length < 3) return [];
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 3 <= normalized.Length; i++) set.Add(normalized.Substring(i, 3));
        return [.. set];
    }

    private static string SortTokens(string normalized)
    {
        var t = TextNormalizer.Tokenize(normalized);
        Array.Sort(t, StringComparer.Ordinal);
        return string.Join(' ', t);
    }

    private static IEnumerable<string?> MechanicTexts(Mechanic m)
    {
        yield return m.Title;
        yield return m.Summary;
        yield return m.ExpectedResult;
        yield return m.ExpectedStateChange;
        yield return m.TurnConditions;
        foreach (var p in m.Prerequisites) { yield return p.Description; yield return p.State; }
        foreach (var st in m.Sequences.SelectMany(s => s.Steps)) { yield return st.Action; yield return st.Target; yield return st.RequiredState; }
        foreach (var n in m.Notes) yield return n.Text;
    }
}
