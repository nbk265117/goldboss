using System.Globalization;
using System.Text.Json;
using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Knowledge;
using DofusCompanion.Application.Matching;
using DofusCompanion.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DofusCompanion.Infrastructure.Persistence;

/// <summary>
/// SQLite implementation of the knowledge store. All multi-row writes run in a single transaction.
/// Calls are synchronous ADO.NET wrapped in Task.Run so callers on the UI thread never block.
/// </summary>
public sealed class SqliteKnowledgeRepository : IKnowledgeRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteKnowledgeRepository> _logger;

    static SqliteKnowledgeRepository()
    {
        // Use the SQLite engine that ships with Windows (System32\winsqlite3.dll, Microsoft-signed).
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
    }

    public SqliteKnowledgeRepository(string databasePath, ILogger<SqliteKnowledgeRepository> logger)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>Creates the file if needed, checks integrity and applies migrations. Call once at startup.</summary>
    public void Initialize(int? targetVersion = null)
    {
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var c = Open();
        try
        {
            using var wal = c.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            // The first statement on a non-database file fails here ("file is not a database").
            throw new DatabaseCorruptException($"The database file could not be read: {ex.Message}", ex);
        }

        DatabaseMigrator.Migrate(c, _logger, targetVersion);
    }

    /// <summary>Releases pooled connections so the file can be moved or deleted.</summary>
    public static void ReleaseFileHandles() => SqliteConnection.ClearAllPools();

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private Task<T> Run<T>(Func<SqliteConnection, T> work, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var c = Open();
        return work(c);
    }, ct);

    private Task Write(Action<SqliteConnection, SqliteTransaction> work, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var c = Open();
        using var tx = c.BeginTransaction();
        try
        {
            work(c, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }, ct);

    // ---------------------------------------------------------------- reads

    public Task<IReadOnlyList<GameEntity>> GetAllEntitiesAsync(CancellationToken ct = default) =>
        Run(c => LoadEntities(c, null), ct);

    public Task<GameEntity?> GetEntityAsync(string id, CancellationToken ct = default) =>
        Run(c => LoadEntities(c, id) is [var first, ..] ? first : null, ct);

    private static IReadOnlyList<GameEntity> LoadEntities(SqliteConnection c, string? onlyId)
    {
        var where = onlyId is null ? "" : " WHERE entity_id = $id";
        var aliases = new Dictionary<string, List<EntityAlias>>(StringComparer.Ordinal);
        foreach (var r in Query(c, "SELECT entity_id, alias, locale, origin FROM entity_aliases" + where + " ORDER BY rowid", onlyId))
            Bucket(aliases, r.GetString(0)).Add(new EntityAlias(r.GetString(1), Str(r, 2), Enum.Parse<RecordOrigin>(r.GetString(3))));

        var attrs = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var r in Query(c, "SELECT entity_id, key, value FROM entity_attributes" + where, onlyId))
        {
            if (!attrs.TryGetValue(r.GetString(0), out var d)) attrs[r.GetString(0)] = d = new(StringComparer.Ordinal);
            d[r.GetString(1)] = r.GetString(2);
        }

        var sources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in Query(c, "SELECT entity_id, source_id FROM entity_sources" + where + " ORDER BY rowid", onlyId))
            Bucket(sources, r.GetString(0)).Add(r.GetString(1));

        var list = new List<GameEntity>();
        foreach (var r in Query(c, "SELECT id, type, canonical_name, locale, description, level_text, verification, version_scope, origin FROM entities"
                                   + (onlyId is null ? "" : " WHERE id = $id"), onlyId))
        {
            var id = r.GetString(0);
            list.Add(new GameEntity
            {
                Id = id,
                Type = Enum.Parse<EntityType>(r.GetString(1)),
                CanonicalName = r.GetString(2),
                Locale = r.GetString(3),
                Description = Str(r, 4),
                LevelText = Str(r, 5),
                Verification = Enum.Parse<VerificationStatus>(r.GetString(6)),
                VersionScope = Str(r, 7),
                Origin = Enum.Parse<RecordOrigin>(r.GetString(8)),
                Aliases = aliases.GetValueOrDefault(id) ?? [],
                Attributes = attrs.GetValueOrDefault(id) ?? new Dictionary<string, string>(),
                SourceIds = sources.GetValueOrDefault(id) ?? [],
            });
        }

        return list;
    }

    public Task<IReadOnlyList<EntityRelation>> GetRelationsAsync(string entityId, CancellationToken ct = default) =>
        Run<IReadOnlyList<EntityRelation>>(c => Query(c, "SELECT from_id, type, to_id FROM entity_relations WHERE from_id = $id OR to_id = $id", entityId)
            .Select(ReadRelation).ToList(), ct);

    public Task<IReadOnlyList<EntityRelation>> GetAllRelationsAsync(CancellationToken ct = default) =>
        Run<IReadOnlyList<EntityRelation>>(c => Query(c, "SELECT from_id, type, to_id FROM entity_relations", null).Select(ReadRelation).ToList(), ct);

    private static EntityRelation ReadRelation(SqliteDataReader r) =>
        new(r.GetString(0), Enum.Parse<RelationType>(r.GetString(1)), r.GetString(2));

    public Task<IReadOnlyList<Mechanic>> GetMechanicsForEntityAsync(string entityId, CancellationToken ct = default) =>
        Run(c => LoadMechanics(c, entityId), ct);

    public Task<IReadOnlyList<Mechanic>> GetAllMechanicsAsync(CancellationToken ct = default) =>
        Run(c => LoadMechanics(c, null), ct);

    private static IReadOnlyList<Mechanic> LoadMechanics(SqliteConnection c, string? entityId)
    {
        // Child tables are filtered through the parent so one entity's load stays cheap.
        var filter = entityId is null ? "" : " WHERE mechanic_id IN (SELECT id FROM mechanics WHERE entity_id = $id)";

        var prereqs = new Dictionary<string, List<Condition>>(StringComparer.Ordinal);
        foreach (var r in Query(c, "SELECT mechanic_id, kind, description, entity_id, state, number FROM mechanic_prerequisites" + filter + " ORDER BY mechanic_id, ord", entityId))
            Bucket(prereqs, r.GetString(0)).Add(ReadCondition(r, 1)!);

        var steps = new Dictionary<string, SortedDictionary<int, (string? Label, List<MechanicStep> Steps)>>(StringComparer.Ordinal);
        foreach (var r in Query(c, "SELECT mechanic_id, seq_index, seq_label, step_order, action, target, position, required_state, " +
                                   "when_kind, when_description, when_entity_id, when_state, when_number, rationale, verification " +
                                   "FROM mechanic_steps" + filter + " ORDER BY mechanic_id, seq_index, step_order", entityId))
        {
            if (!steps.TryGetValue(r.GetString(0), out var seqs)) steps[r.GetString(0)] = seqs = new();
            var idx = r.GetInt32(1);
            if (!seqs.TryGetValue(idx, out var seq)) seqs[idx] = seq = (Str(r, 2), []);
            seq.Steps.Add(new MechanicStep
            {
                Order = r.GetInt32(3),
                Action = r.GetString(4),
                Target = Str(r, 5),
                Position = Str(r, 6),
                RequiredState = Str(r, 7),
                When = ReadCondition(r, 8),
                Rationale = Str(r, 13),
                Verification = Str(r, 14) is { } v ? Enum.Parse<VerificationStatus>(v) : null,
            });
        }

        var notes = new Dictionary<string, List<MechanicNote>>(StringComparer.Ordinal);
        foreach (var r in Query(c, "SELECT mechanic_id, kind, text FROM mechanic_notes" + filter + " ORDER BY mechanic_id, ord", entityId))
            Bucket(notes, r.GetString(0)).Add(new MechanicNote(Enum.Parse<NoteKind>(r.GetString(1)), r.GetString(2)));

        var unknowns = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in Query(c, "SELECT mechanic_id, text FROM mechanic_unknowns" + filter + " ORDER BY mechanic_id, ord", entityId))
            Bucket(unknowns, r.GetString(0)).Add(r.GetString(1));

        var refs = new Dictionary<string, List<SourceReference>>(StringComparer.Ordinal);
        foreach (var r in Query(c, "SELECT mechanic_id, source_id, quote, locator FROM mechanic_sources" + filter + " ORDER BY mechanic_id, ord", entityId))
            Bucket(refs, r.GetString(0)).Add(new SourceReference(r.GetString(1), Str(r, 2), Str(r, 3)));

        var list = new List<Mechanic>();
        foreach (var r in Query(c, "SELECT id, entity_id, type, title, summary, verification, expected_state_change, expected_result, " +
                                   "vulnerability_duration, turn_conditions, version_scope, last_verified_on, origin FROM mechanics" +
                                   (entityId is null ? "" : " WHERE entity_id = $id") + " ORDER BY id", entityId))
        {
            var id = r.GetString(0);
            list.Add(new Mechanic
            {
                Id = id,
                EntityId = r.GetString(1),
                Type = Enum.Parse<MechanicType>(r.GetString(2)),
                Title = r.GetString(3),
                Summary = Str(r, 4),
                Verification = Enum.Parse<VerificationStatus>(r.GetString(5)),
                ExpectedStateChange = Str(r, 6),
                ExpectedResult = Str(r, 7),
                VulnerabilityDuration = Str(r, 8),
                TurnConditions = Str(r, 9),
                VersionScope = Str(r, 10),
                LastVerifiedOn = Date(r, 11),
                Origin = Enum.Parse<RecordOrigin>(r.GetString(12)),
                Prerequisites = prereqs.GetValueOrDefault(id) ?? [],
                Sequences = steps.TryGetValue(id, out var s)
                    ? s.Values.Select(x => new ActionSequence { Label = x.Label, Steps = x.Steps }).ToList()
                    : [],
                Notes = notes.GetValueOrDefault(id) ?? [],
                UnknownAspects = unknowns.GetValueOrDefault(id) ?? [],
                Sources = refs.GetValueOrDefault(id) ?? [],
            });
        }

        return list;
    }

    private static Condition? ReadCondition(SqliteDataReader r, int start)
    {
        if (r.IsDBNull(start)) return null;
        return new Condition
        {
            Kind = Enum.Parse<ConditionKind>(r.GetString(start)),
            Description = r.GetString(start + 1),
            EntityId = Str(r, start + 2),
            State = Str(r, start + 3),
            Number = r.IsDBNull(start + 4) ? null : r.GetInt32(start + 4),
        };
    }

    public Task<IReadOnlyList<KnowledgeSource>> GetAllSourcesAsync(CancellationToken ct = default) =>
        Run<IReadOnlyList<KnowledgeSource>>(c => Query(c,
            "SELECT id, title, url, publisher, category, reliability, content_updated_on, retrieved_on, last_checked_on, notes FROM sources", null)
            .Select(r => new KnowledgeSource
            {
                Id = r.GetString(0),
                Title = r.GetString(1),
                Url = Str(r, 2),
                Publisher = Str(r, 3),
                Category = Enum.Parse<SourceCategory>(r.GetString(4)),
                Reliability = Enum.Parse<SourceReliability>(r.GetString(5)),
                ContentUpdatedOn = Date(r, 6),
                RetrievedOn = Date(r, 7),
                LastCheckedOn = Date(r, 8),
                Notes = Str(r, 9),
            }).ToList(), ct);

    public Task<IReadOnlyList<GameVersion>> GetGameVersionsAsync(CancellationToken ct = default) =>
        Run<IReadOnlyList<GameVersion>>(c => Query(c, "SELECT id, version, release_date, source_id, verified_on, notes FROM game_versions", null)
            .Select(r => new GameVersion
            {
                Id = r.GetString(0),
                Version = Str(r, 1),
                ReleaseDate = Date(r, 2),
                SourceId = Str(r, 3),
                VerifiedOn = Date(r, 4),
                Notes = Str(r, 5),
            }).ToList(), ct);

    public Task<DatabaseStatus> GetStatusAsync(CancellationToken ct = default) => Run(c =>
    {
        long Count(string table)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)cmd.ExecuteScalar()!;
        }

        return new DatabaseStatus(DatabasePath, DatabaseMigrator.GetVersion(c), (int)Count("entities"), (int)Count("mechanics"),
            (int)Count("sources"), File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0);
    }, ct);

    public Task<IReadOnlyList<(string PackageId, string Name, DateTimeOffset ImportedAt)>> GetImportLogAsync(CancellationToken ct = default) =>
        Run<IReadOnlyList<(string, string, DateTimeOffset)>>(c => Query(c, "SELECT package_id, package_name, imported_at FROM import_log ORDER BY id DESC", null)
            .Select(r => (r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture))).ToList(), ct);

    // ---------------------------------------------------------------- writes

    public Task ApplyImportAsync(ImportPlan plan, CancellationToken ct = default) => Write((c, tx) =>
    {
        int added = 0, updated = 0, conflicts = 0, skipped = 0;

        // Order matters for foreign keys: versions, sources, entities (rows first, then relations), mechanics.
        foreach (var change in plan.Changes.OrderBy(ch => ch.Kind))
        {
            var record = change.ToWrite;
            switch (change.Change)
            {
                case ChangeKind.Add: added++; break;
                case ChangeKind.Update: updated++; break;
                case ChangeKind.Conflict when record is not null: conflicts++; break;
                case ChangeKind.Skip: skipped++; break;
            }

            switch (record)
            {
                case GameVersion v: UpsertVersion(c, tx, v); break;
                case KnowledgeSource s: UpsertSource(c, tx, s); break;
                case PackageEntity pe: UpsertEntityRow(c, tx, pe.Entity); break;
                case Mechanic: break; // written after all entities exist
            }
        }

        foreach (var pe in plan.Changes.Where(ch => ch.Kind == RecordKind.Entity).Select(ch => ch.ToWrite).OfType<PackageEntity>())
            ReplaceRelations(c, tx, pe.Entity.Id, pe.Relations);

        foreach (var m in plan.Changes.Where(ch => ch.Kind == RecordKind.Mechanic).Select(ch => ch.ToWrite).OfType<Mechanic>())
            UpsertMechanic(c, tx, m);

        Exec(c, tx, "INSERT INTO import_log(package_id, package_name, imported_at, added, updated, conflicts_applied, skipped, content_hash) " +
                    "VALUES ($p, $n, $t, $a, $u, $c, $s, $h)",
            ("$p", plan.Package.PackageId), ("$n", plan.Package.Name), ("$t", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)),
            ("$a", added), ("$u", updated), ("$c", conflicts), ("$s", skipped), ("$h", plan.ContentHash));

        _logger.LogInformation("Imported package {Package}: {Added} added, {Updated} updated, {Conflicts} conflicts applied, {Skipped} skipped",
            plan.Package.PackageId, added, updated, conflicts, skipped);
    }, ct);

    public Task<bool> WasImportedAsync(string packageId, string contentHash, CancellationToken ct = default) => Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM import_log WHERE package_id = $p AND content_hash = $h";
        cmd.Parameters.AddWithValue("$p", packageId);
        cmd.Parameters.AddWithValue("$h", contentHash);
        return (long)cmd.ExecuteScalar()! > 0;
    }, ct);

    public Task UpsertSourceAsync(KnowledgeSource source, CancellationToken ct = default) =>
        Write((c, tx) => UpsertSource(c, tx, source), ct);

    public Task UpsertEntityAsync(GameEntity entity, IReadOnlyList<EntityRelation> relations, CancellationToken ct = default) =>
        Write((c, tx) =>
        {
            UpsertEntityRow(c, tx, entity);
            ReplaceRelations(c, tx, entity.Id, relations);
        }, ct);

    public Task UpsertMechanicAsync(Mechanic mechanic, CancellationToken ct = default) =>
        Write((c, tx) => UpsertMechanic(c, tx, mechanic), ct);

    public Task AddAliasAsync(string entityId, EntityAlias alias, CancellationToken ct = default) =>
        Write((c, tx) => Exec(c, tx,
            "INSERT OR IGNORE INTO entity_aliases(entity_id, alias, normalized, locale, origin) VALUES ($e, $a, $n, $l, $o)",
            ("$e", entityId), ("$a", alias.Text), ("$n", TextNormalizer.Normalize(alias.Text)), ("$l", alias.Locale), ("$o", alias.Origin.ToString())), ct);

    public Task RecordObservationAsync(RecognitionObservation observation, CancellationToken ct = default) =>
        Write((c, tx) => Exec(c, tx,
            "INSERT OR REPLACE INTO observations(id, timestamp, method, ocr_text, region, candidates, confirmation, confirmed_entity_id, screenshot_path) " +
            "VALUES ($id, $ts, $m, $t, $r, $c, $cf, $ce, $sp)",
            ("$id", observation.Id.ToString()), ("$ts", observation.Timestamp.ToString("O", CultureInfo.InvariantCulture)), ("$m", observation.Method.ToString()),
            ("$t", observation.OcrText), ("$r", observation.Region is { } r ? JsonSerializer.Serialize(r) : null),
            ("$c", JsonSerializer.Serialize(observation.CandidateEntityIds.Zip(observation.CandidateScores, (id, s) => new { id, s }))),
            ("$cf", observation.Confirmation.ToString()), ("$ce", observation.ConfirmedEntityId), ("$sp", observation.ScreenshotPath)), ct);

    public Task<int> DeleteObservationsAsync(CancellationToken ct = default) => Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM observations";
        return cmd.ExecuteNonQuery();
    }, ct);

    private static void UpsertVersion(SqliteConnection c, SqliteTransaction tx, GameVersion v) => Exec(c, tx,
        "INSERT INTO game_versions(id, version, release_date, source_id, verified_on, notes) VALUES ($id, $v, $r, $s, $vo, $n) " +
        "ON CONFLICT(id) DO UPDATE SET version=excluded.version, release_date=excluded.release_date, source_id=excluded.source_id, " +
        "verified_on=excluded.verified_on, notes=excluded.notes",
        ("$id", v.Id), ("$v", v.Version), ("$r", Iso(v.ReleaseDate)), ("$s", v.SourceId), ("$vo", Iso(v.VerifiedOn)), ("$n", v.Notes));

    private static void UpsertSource(SqliteConnection c, SqliteTransaction tx, KnowledgeSource s) => Exec(c, tx,
        "INSERT INTO sources(id, title, url, publisher, category, reliability, content_updated_on, retrieved_on, last_checked_on, notes) " +
        "VALUES ($id, $t, $u, $p, $c, $r, $cu, $ro, $lc, $n) ON CONFLICT(id) DO UPDATE SET title=excluded.title, url=excluded.url, " +
        "publisher=excluded.publisher, category=excluded.category, reliability=excluded.reliability, content_updated_on=excluded.content_updated_on, " +
        "retrieved_on=excluded.retrieved_on, last_checked_on=excluded.last_checked_on, notes=excluded.notes",
        ("$id", s.Id), ("$t", s.Title), ("$u", s.Url), ("$p", s.Publisher), ("$c", s.Category.ToString()), ("$r", s.Reliability.ToString()),
        ("$cu", Iso(s.ContentUpdatedOn)), ("$ro", Iso(s.RetrievedOn)), ("$lc", Iso(s.LastCheckedOn)), ("$n", s.Notes));

    private static void UpsertEntityRow(SqliteConnection c, SqliteTransaction tx, GameEntity e)
    {
        // ON CONFLICT DO UPDATE (not INSERT OR REPLACE): replacing would cascade-delete the entity's mechanics.
        Exec(c, tx,
            "INSERT INTO entities(id, type, canonical_name, normalized_name, locale, description, level_text, verification, version_scope, origin, updated_at) " +
            "VALUES ($id, $ty, $n, $nn, $l, $d, $lv, $v, $vs, $o, $u) ON CONFLICT(id) DO UPDATE SET type=excluded.type, " +
            "canonical_name=excluded.canonical_name, normalized_name=excluded.normalized_name, locale=excluded.locale, " +
            "description=excluded.description, level_text=excluded.level_text, verification=excluded.verification, " +
            "version_scope=excluded.version_scope, origin=excluded.origin, updated_at=excluded.updated_at",
            ("$id", e.Id), ("$ty", e.Type.ToString()), ("$n", e.CanonicalName), ("$nn", TextNormalizer.Normalize(e.CanonicalName)),
            ("$l", e.Locale), ("$d", e.Description), ("$lv", e.LevelText), ("$v", e.Verification.ToString()), ("$vs", e.VersionScope),
            ("$o", e.Origin.ToString()), ("$u", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)));

        Exec(c, tx, "DELETE FROM entity_aliases WHERE entity_id = $id", ("$id", e.Id));
        foreach (var a in e.Aliases)
            Exec(c, tx, "INSERT OR IGNORE INTO entity_aliases(entity_id, alias, normalized, locale, origin) VALUES ($e, $a, $n, $l, $o)",
                ("$e", e.Id), ("$a", a.Text), ("$n", TextNormalizer.Normalize(a.Text)), ("$l", a.Locale), ("$o", a.Origin.ToString()));

        Exec(c, tx, "DELETE FROM entity_attributes WHERE entity_id = $id", ("$id", e.Id));
        foreach (var (k, v) in e.Attributes)
            Exec(c, tx, "INSERT INTO entity_attributes(entity_id, key, value) VALUES ($e, $k, $v)", ("$e", e.Id), ("$k", k), ("$v", v));

        Exec(c, tx, "DELETE FROM entity_sources WHERE entity_id = $id", ("$id", e.Id));
        foreach (var s in e.SourceIds.Distinct(StringComparer.Ordinal))
            Exec(c, tx, "INSERT INTO entity_sources(entity_id, source_id) VALUES ($e, $s)", ("$e", e.Id), ("$s", s));
    }

    private static void ReplaceRelations(SqliteConnection c, SqliteTransaction tx, string fromId, IReadOnlyList<EntityRelation> relations)
    {
        Exec(c, tx, "DELETE FROM entity_relations WHERE from_id = $id", ("$id", fromId));
        foreach (var r in relations.Distinct())
            Exec(c, tx, "INSERT OR IGNORE INTO entity_relations(from_id, type, to_id) VALUES ($f, $t, $to)",
                ("$f", fromId), ("$t", r.Type.ToString()), ("$to", r.ToId));
    }

    private static void UpsertMechanic(SqliteConnection c, SqliteTransaction tx, Mechanic m)
    {
        Exec(c, tx,
            "INSERT INTO mechanics(id, entity_id, type, title, summary, verification, expected_state_change, expected_result, vulnerability_duration, " +
            "turn_conditions, version_scope, last_verified_on, origin, updated_at) VALUES ($id, $e, $t, $ti, $s, $v, $esc, $er, $vd, $tc, $vs, $lv, $o, $u) " +
            "ON CONFLICT(id) DO UPDATE SET entity_id=excluded.entity_id, type=excluded.type, title=excluded.title, summary=excluded.summary, " +
            "verification=excluded.verification, expected_state_change=excluded.expected_state_change, expected_result=excluded.expected_result, " +
            "vulnerability_duration=excluded.vulnerability_duration, turn_conditions=excluded.turn_conditions, version_scope=excluded.version_scope, " +
            "last_verified_on=excluded.last_verified_on, origin=excluded.origin, updated_at=excluded.updated_at",
            ("$id", m.Id), ("$e", m.EntityId), ("$t", m.Type.ToString()), ("$ti", m.Title), ("$s", m.Summary), ("$v", m.Verification.ToString()),
            ("$esc", m.ExpectedStateChange), ("$er", m.ExpectedResult), ("$vd", m.VulnerabilityDuration), ("$tc", m.TurnConditions),
            ("$vs", m.VersionScope), ("$lv", Iso(m.LastVerifiedOn)), ("$o", m.Origin.ToString()),
            ("$u", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)));

        foreach (var table in new[] { "mechanic_prerequisites", "mechanic_steps", "mechanic_notes", "mechanic_unknowns", "mechanic_sources" })
            Exec(c, tx, $"DELETE FROM {table} WHERE mechanic_id = $id", ("$id", m.Id));

        for (var i = 0; i < m.Prerequisites.Count; i++)
        {
            var p = m.Prerequisites[i];
            Exec(c, tx, "INSERT INTO mechanic_prerequisites(mechanic_id, ord, kind, description, entity_id, state, number) VALUES ($m, $o, $k, $d, $e, $s, $n)",
                ("$m", m.Id), ("$o", i), ("$k", p.Kind.ToString()), ("$d", p.Description), ("$e", p.EntityId), ("$s", p.State), ("$n", p.Number));
        }

        for (var si = 0; si < m.Sequences.Count; si++)
        {
            var seq = m.Sequences[si];
            foreach (var st in seq.Steps)
            {
                Exec(c, tx,
                    "INSERT INTO mechanic_steps(mechanic_id, seq_index, seq_label, step_order, action, target, position, required_state, when_kind, " +
                    "when_description, when_entity_id, when_state, when_number, rationale, verification) " +
                    "VALUES ($m, $si, $sl, $o, $a, $t, $p, $rs, $wk, $wd, $we, $ws, $wn, $r, $v)",
                    ("$m", m.Id), ("$si", si), ("$sl", seq.Label), ("$o", st.Order), ("$a", st.Action), ("$t", st.Target), ("$p", st.Position),
                    ("$rs", st.RequiredState), ("$wk", st.When?.Kind.ToString()), ("$wd", st.When?.Description), ("$we", st.When?.EntityId),
                    ("$ws", st.When?.State), ("$wn", st.When?.Number), ("$r", st.Rationale), ("$v", st.Verification?.ToString()));
            }
        }

        for (var i = 0; i < m.Notes.Count; i++)
            Exec(c, tx, "INSERT INTO mechanic_notes(mechanic_id, ord, kind, text) VALUES ($m, $o, $k, $t)",
                ("$m", m.Id), ("$o", i), ("$k", m.Notes[i].Kind.ToString()), ("$t", m.Notes[i].Text));

        for (var i = 0; i < m.UnknownAspects.Count; i++)
            Exec(c, tx, "INSERT INTO mechanic_unknowns(mechanic_id, ord, text) VALUES ($m, $o, $t)", ("$m", m.Id), ("$o", i), ("$t", m.UnknownAspects[i]));

        // Several quotes may cite the same source, hence the ordinal key.
        for (var i = 0; i < m.Sources.Count; i++)
            Exec(c, tx, "INSERT INTO mechanic_sources(mechanic_id, ord, source_id, quote, locator) VALUES ($m, $o, $s, $q, $l)",
                ("$m", m.Id), ("$o", i), ("$s", m.Sources[i].SourceId), ("$q", m.Sources[i].Quote), ("$l", m.Sources[i].Locator));
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<SqliteDataReader> Query(SqliteConnection c, string sql, string? id)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (id is not null) cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            yield return reader;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static List<T> Bucket<T>(Dictionary<string, List<T>> d, string key)
    {
        if (!d.TryGetValue(key, out var list)) d[key] = list = [];
        return list;
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateOnly? Date(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateOnly.ParseExact(r.GetString(i), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
