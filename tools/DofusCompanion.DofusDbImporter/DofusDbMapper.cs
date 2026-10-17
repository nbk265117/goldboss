using System.Globalization;
using System.Text.Json;
using DofusCompanion.Application.Knowledge;
using DofusCompanion.Domain;

namespace DofusCompanion.DofusDbImporter;

/// <summary>Raw DofusDB records grouped by endpoint (each element is one JSON object from a "data" array).</summary>
public sealed record DofusDbSnapshot(
    IReadOnlyList<JsonElement> Dungeons,
    IReadOnlyList<JsonElement> Monsters,
    IReadOnlyList<JsonElement> Races,
    IReadOnlyList<JsonElement> SubAreas,
    IReadOnlyList<JsonElement> Areas);

/// <summary>
/// Converts DofusDB records into a knowledge package of ENTITIES only (names, aliases, levels, families,
/// locations). It never produces mechanics: DofusDB lists game data, not boss strategies.
/// Every field is read defensively; a record missing its id or name is skipped and counted.
/// </summary>
public static class DofusDbMapper
{
    public const string SourceId = "src.dofusdb";
    private static readonly string[] Languages = ["fr", "en", "es", "de", "pt"];

    public sealed record Result(KnowledgePackage Package, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Picture URL of a monster record: its "img" field when it is an https URL on dofusdb.fr, otherwise the
    /// given template ("{id}" replaced by the game id). Anything else is ignored.
    /// </summary>
    public static Uri? ImageUrl(JsonElement monster, string? template)
    {
        if (Int(monster, "id") is not { } id) return null;
        foreach (var field in new[] { "img", "image" })
        {
            if (monster.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                && Uri.TryCreate(v.GetString(), UriKind.Absolute, out var u) && IsAllowedImageHost(u))
                return u;
        }

        return template is not null && Uri.TryCreate(template.Replace("{id}", id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), UriKind.Absolute, out var t)
               && IsAllowedImageHost(t) ? t : null;
    }

    private static bool IsAllowedImageHost(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps && (u.Host.Equals("dofusdb.fr", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".dofusdb.fr", StringComparison.OrdinalIgnoreCase));

    public static string ImageRelativePath(int gameId) => $"dofusdb/{gameId}.png";

    /// <param name="imageFor">Returns the picture path (relative to the app's images folder) for a monster game id, or null.</param>
    public static Result Map(DofusDbSnapshot s, DateOnly today, string primaryLanguage = "fr", Func<int, string?>? imageFor = null)
    {
        var warnings = new List<string>();
        var entities = new Dictionary<string, PackageEntity>(StringComparer.Ordinal);
        var source = new KnowledgeSource
        {
            Id = SourceId,
            Title = "DofusDB — base de données Dofus",
            Url = "https://dofusdb.fr/fr/database/monsters",
            Publisher = "DofusDB (site communautaire)",
            Category = SourceCategory.Wiki,
            Reliability = SourceReliability.Community,
            RetrievedOn = today,
            LastCheckedOn = today,
            Notes = "Données de jeu reprises par un site communautaire, importées par l'utilisateur avec l'outil dofusdb-import. Non vérifiées manuellement.",
        };

        void Add(GameEntity e, IEnumerable<EntityRelation> relations)
        {
            if (!entities.TryAdd(e.Id, new PackageEntity(e, relations.Where(r => r.ToId != e.Id).Distinct().ToList())))
                warnings.Add($"Duplicate id {e.Id} ignored.");
        }

        GameEntity Entity(string id, EntityType type, JsonElement record, string? levelText = null, Dictionary<string, string>? attributes = null)
        {
            var names = Names(record);
            var canonical = names.GetValueOrDefault(primaryLanguage) ?? names.Values.First();
            return new GameEntity
            {
                Id = id,
                Type = type,
                CanonicalName = canonical.Trim(),
                Locale = names.ContainsKey(primaryLanguage) ? primaryLanguage : names.Keys.First(),
                Aliases = names
                    .Where(kv => !string.Equals(kv.Value.Trim(), canonical.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Select(kv => new EntityAlias(kv.Value.Trim(), kv.Key, RecordOrigin.Import))
                    .DistinctBy(a => a.Text.ToUpperInvariant())
                    .Take(50)
                    .ToList(),
                LevelText = levelText,
                Attributes = attributes ?? new Dictionary<string, string>(),
                Verification = VerificationStatus.CommunityReported,
                SourceIds = [SourceId],
                Origin = RecordOrigin.Import,
            };
        }

        bool Valid(JsonElement r, string kind)
        {
            if (Int(r, "id") is null || Names(r).Count == 0)
            {
                warnings.Add($"{kind} record without id or name skipped.");
                return false;
            }

            return true;
        }

        // Areas and sub-areas (dimensions such as Enutrosor or Srambad are areas in the game data).
        foreach (var a in s.Areas.Where(a => Valid(a, "area")))
            Add(Entity(AreaId(Int(a, "id")!.Value), EntityType.Area, a, attributes: GameId(a)), []);

        foreach (var sa in s.SubAreas.Where(sa => Valid(sa, "subarea")))
        {
            var parent = Int(sa, "areaId");
            Add(Entity(SubAreaId(Int(sa, "id")!.Value), EntityType.Area, sa, attributes: GameId(sa)),
                parent is { } p && entities.ContainsKey(AreaId(p)) ? [new EntityRelation(SubAreaId(Int(sa, "id")!.Value), RelationType.PartOf, AreaId(p))] : []);
        }

        foreach (var r in s.Races.Where(r => Valid(r, "race")))
            Add(Entity(FamilyId(Int(r, "id")!.Value), EntityType.MonsterFamily, r, attributes: GameId(r)), []);

        // Dungeons, and which monsters/bosses belong to them.
        var dungeonOfMonster = new Dictionary<int, string>();
        foreach (var d in s.Dungeons.Where(d => Valid(d, "dungeon")))
        {
            var id = DungeonId(Int(d, "id")!.Value);
            var attrs = GameId(d);
            if (Int(d, "minLevel") is { } min) attrs["minLevel"] = min.ToString(CultureInfo.InvariantCulture);
            if (Int(d, "optimalPlayerLevel") is { } opt) attrs["optimalPlayerLevel"] = opt.ToString(CultureInfo.InvariantCulture);
            var sub = Int(d, "subarea");
            Add(Entity(id, EntityType.Dungeon, d, levelText: attrs.GetValueOrDefault("optimalPlayerLevel"), attributes: attrs),
                sub is { } sid && entities.ContainsKey(SubAreaId(sid)) ? [new EntityRelation(id, RelationType.PartOf, SubAreaId(sid))] : []);
            foreach (var m in IntArray(d, "monsters").Concat(IntArray(d, "bosses")))
                dungeonOfMonster.TryAdd(m, id);
        }

        foreach (var m in s.Monsters.Where(m => Valid(m, "monster")))
        {
            var gameId = Int(m, "id")!.Value;
            var isBoss = IsBoss(m);
            var id = isBoss ? $"boss.dofusdb-{gameId}" : $"monster.dofusdb-{gameId}";
            var levels = Levels(m);
            var levelText = levels.Count == 0 ? null : levels.Min() == levels.Max() ? $"{levels.Min()}" : $"{levels.Min()}-{levels.Max()}";
            var relations = new List<EntityRelation>();
            if (Int(m, "race") is { } race && entities.ContainsKey(FamilyId(race)))
                relations.Add(new EntityRelation(id, RelationType.MemberOfFamily, FamilyId(race)));
            if (dungeonOfMonster.TryGetValue(gameId, out var dungeon))
                relations.Add(new EntityRelation(id, RelationType.LocatedIn, dungeon));
            else
                relations.AddRange(IntArray(m, "subareas").Select(SubAreaId).Where(entities.ContainsKey).Take(20)
                    .Select(sa => new EntityRelation(id, RelationType.LocatedIn, sa)));
            var attributes = GameId(m);
            if (imageFor?.Invoke(gameId) is { } picture) attributes["image"] = picture;
            Add(Entity(id, isBoss ? EntityType.Boss : EntityType.Monster, m, levelText, attributes), relations);
        }

        // A dungeon has no picture of its own on DofusDB: show its (first) boss's one.
        foreach (var d in s.Dungeons.Where(d => Int(d, "id") is not null))
        {
            if (!entities.TryGetValue(DungeonId(Int(d, "id")!.Value), out var pe) || pe.Entity.Attributes.ContainsKey("image")) continue;
            var picture = IntArray(d, "bosses").Select(b => imageFor?.Invoke(b)).FirstOrDefault(p => p is not null);
            if (picture is not null)
                entities[pe.Entity.Id] = pe with { Entity = pe.Entity with { Attributes = new Dictionary<string, string>(pe.Entity.Attributes) { ["image"] = picture } } };
        }

        // Relations may point to entities added later in the loop order; drop any that still dangle.
        var package = new KnowledgePackage
        {
            PackageId = $"dofusdb-{today:yyyyMMdd}",
            Name = $"DofusDB — monstres, boss, donjons et zones ({today:yyyy-MM-dd})",
            Description = "Entités uniquement (aucune mécanique). Importé par l'utilisateur depuis DofusDB ; statut « rapporté par la communauté ».",
            Author = "dofusdb-import",
            CreatedOn = today,
            Sources = [source],
            Entities = entities.Values
                .Select(pe => pe with { Relations = pe.Relations.Where(r => entities.ContainsKey(r.ToId)).ToList() })
                .ToList(),
        };
        return new Result(package, warnings);
    }

    public static string AreaId(int id) => $"area.dofusdb-{id}";
    public static string SubAreaId(int id) => $"area.dofusdb-sub-{id}";
    public static string FamilyId(int id) => $"family.dofusdb-{id}";
    public static string DungeonId(int id) => $"dungeon.dofusdb-{id}";

    private static Dictionary<string, string> GameId(JsonElement r) =>
        new(StringComparer.Ordinal) { ["gameId"] = Int(r, "id")!.Value.ToString(CultureInfo.InvariantCulture) };

    /// <summary>Localized names: { "name": { "fr": "...", "en": "..." } } or a plain string.</summary>
    internal static Dictionary<string, string> Names(JsonElement r)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!r.TryGetProperty("name", out var name)) return result;
        if (name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
        {
            result["fr"] = name.GetString()!;
            return result;
        }

        if (name.ValueKind != JsonValueKind.Object) return result;
        foreach (var lang in Languages)
            if (name.TryGetProperty(lang, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) && v.GetString()!.Length <= 200)
                result[lang] = v.GetString()!;
        return result;
    }

    private static List<int> Levels(JsonElement m)
    {
        var levels = new List<int>();
        if (m.TryGetProperty("grades", out var grades) && grades.ValueKind == JsonValueKind.Array)
            foreach (var g in grades.EnumerateArray())
                if (Int(g, "level") is { } l and > 0 and < 10000) levels.Add(l);
        return levels;
    }

    internal static int? Int(JsonElement r, string prop) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    internal static bool IsBoss(JsonElement m) => Bool(m, "isBoss") ?? false;

    private static bool? Bool(JsonElement r, string prop) =>
        r.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static IEnumerable<int> IntArray(JsonElement r, string prop)
    {
        if (!r.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in v.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i)) yield return i;
    }
}
