using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DofusCompanion.Infrastructure.Persistence;

public sealed class DatabaseCorruptException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class DatabaseMigrationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Forward-only schema migrations tracked with PRAGMA user_version. Each migration runs in its own
/// transaction; a failure rolls back and leaves the previous version intact.
/// </summary>
public static class DatabaseMigrator
{
    /// <summary>Ordered migrations; index + 1 is the resulting schema version. Never edit a shipped migration.</summary>
    internal static readonly string[] Migrations =
    [
        // v1: core knowledge model.
        """
        CREATE TABLE game_versions (
            id TEXT PRIMARY KEY, version TEXT, release_date TEXT, source_id TEXT, verified_on TEXT, notes TEXT);

        CREATE TABLE sources (
            id TEXT PRIMARY KEY, title TEXT NOT NULL, url TEXT, publisher TEXT, category TEXT NOT NULL,
            reliability TEXT NOT NULL, content_updated_on TEXT, retrieved_on TEXT, last_checked_on TEXT, notes TEXT);

        CREATE TABLE entities (
            id TEXT PRIMARY KEY, type TEXT NOT NULL, canonical_name TEXT NOT NULL, normalized_name TEXT NOT NULL,
            locale TEXT NOT NULL, description TEXT, level_text TEXT, verification TEXT NOT NULL, version_scope TEXT,
            origin TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE INDEX ix_entities_type_name ON entities(type, normalized_name);

        CREATE TABLE entity_aliases (
            entity_id TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, alias TEXT NOT NULL,
            normalized TEXT NOT NULL, locale TEXT, origin TEXT NOT NULL, PRIMARY KEY (entity_id, normalized));
        CREATE INDEX ix_aliases_normalized ON entity_aliases(normalized);

        CREATE TABLE entity_attributes (
            entity_id TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, key TEXT NOT NULL, value TEXT NOT NULL,
            PRIMARY KEY (entity_id, key));

        CREATE TABLE entity_sources (
            entity_id TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, source_id TEXT NOT NULL,
            PRIMARY KEY (entity_id, source_id));

        CREATE TABLE entity_relations (
            from_id TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, type TEXT NOT NULL,
            to_id TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, PRIMARY KEY (from_id, type, to_id));
        CREATE INDEX ix_relations_to ON entity_relations(to_id);

        CREATE TABLE mechanics (
            id TEXT PRIMARY KEY, entity_id TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, type TEXT NOT NULL,
            title TEXT NOT NULL, summary TEXT, verification TEXT NOT NULL, expected_state_change TEXT, expected_result TEXT,
            vulnerability_duration TEXT, turn_conditions TEXT, version_scope TEXT, last_verified_on TEXT,
            origin TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE INDEX ix_mechanics_entity ON mechanics(entity_id);

        CREATE TABLE mechanic_prerequisites (
            mechanic_id TEXT NOT NULL REFERENCES mechanics(id) ON DELETE CASCADE, ord INTEGER NOT NULL, kind TEXT NOT NULL,
            description TEXT NOT NULL, entity_id TEXT, state TEXT, number INTEGER, PRIMARY KEY (mechanic_id, ord));

        CREATE TABLE mechanic_steps (
            mechanic_id TEXT NOT NULL REFERENCES mechanics(id) ON DELETE CASCADE, seq_index INTEGER NOT NULL,
            seq_label TEXT, step_order INTEGER NOT NULL, action TEXT NOT NULL, target TEXT, position TEXT,
            required_state TEXT, when_kind TEXT, when_description TEXT, when_entity_id TEXT, when_state TEXT,
            when_number INTEGER, rationale TEXT, verification TEXT, PRIMARY KEY (mechanic_id, seq_index, step_order));

        CREATE TABLE mechanic_notes (
            mechanic_id TEXT NOT NULL REFERENCES mechanics(id) ON DELETE CASCADE, ord INTEGER NOT NULL, kind TEXT NOT NULL,
            text TEXT NOT NULL, PRIMARY KEY (mechanic_id, ord));

        CREATE TABLE mechanic_unknowns (
            mechanic_id TEXT NOT NULL REFERENCES mechanics(id) ON DELETE CASCADE, ord INTEGER NOT NULL, text TEXT NOT NULL,
            PRIMARY KEY (mechanic_id, ord));

        CREATE TABLE mechanic_sources (
            mechanic_id TEXT NOT NULL REFERENCES mechanics(id) ON DELETE CASCADE,
            ord INTEGER NOT NULL, source_id TEXT NOT NULL REFERENCES sources(id), quote TEXT, locator TEXT,
            PRIMARY KEY (mechanic_id, ord));

        CREATE TABLE observations (
            id TEXT PRIMARY KEY, timestamp TEXT NOT NULL, method TEXT NOT NULL, ocr_text TEXT, region TEXT,
            candidates TEXT NOT NULL, confirmation TEXT NOT NULL, confirmed_entity_id TEXT, screenshot_path TEXT);
        """,
        // v2: import history (which packages were applied, with counts) for diagnostics and support.
        """
        CREATE TABLE import_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT, package_id TEXT NOT NULL, package_name TEXT NOT NULL,
            imported_at TEXT NOT NULL, added INTEGER NOT NULL, updated INTEGER NOT NULL,
            conflicts_applied INTEGER NOT NULL, skipped INTEGER NOT NULL, content_hash TEXT);
        CREATE INDEX ix_import_log_package ON import_log(package_id);
        """,
    ];

    public static int LatestVersion => Migrations.Length;

    public static int GetVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Checks integrity, then applies pending migrations up to <paramref name="targetVersion"/>.</summary>
    public static void Migrate(SqliteConnection connection, ILogger logger, int? targetVersion = null)
    {
        var target = targetVersion ?? LatestVersion;
        CheckIntegrity(connection);

        var current = GetVersion(connection);
        if (current > LatestVersion)
            throw new DatabaseMigrationException(
                $"Database schema version {current} is newer than this application supports ({LatestVersion}). Update the application.");

        for (var v = current; v < target; v++)
        {
            logger.LogInformation("Applying database migration {From} -> {To}", v, v + 1);
            using var tx = connection.BeginTransaction();
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = Migrations[v] + $"\nPRAGMA user_version = {v + 1};";
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch (SqliteException ex)
            {
                tx.Rollback();
                throw new DatabaseMigrationException($"Migration to schema version {v + 1} failed: {ex.Message}", ex);
            }
        }
    }

    private static void CheckIntegrity(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = cmd.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new DatabaseCorruptException($"Database integrity check failed: {result}");
        }
        catch (SqliteException ex)
        {
            // "file is not a database" (SQLITE_NOTADB = 26) and similar surface here.
            throw new DatabaseCorruptException($"The database file could not be read: {ex.Message}", ex);
        }
    }
}
