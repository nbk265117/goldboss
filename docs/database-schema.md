# Database schema

SQLite file (default `%LOCALAPPDATA%\DofusCompanion\knowledge.db`), WAL journal, foreign keys on.
Schema version = `PRAGMA user_version`. Migrations are in
`src/DofusCompanion.Infrastructure/Persistence/DatabaseMigrator.cs`; each runs in its own transaction and
shipped migrations are never edited.

| Version | Change |
|---|---|
| 1 | Core model (tables below except `import_log`) |
| 2 | `import_log` (package id, name, date, counts, content hash) |

## Tables (v2)

- `game_versions(id PK, version, release_date, source_id, verified_on, notes)`
- `sources(id PK, title, url, publisher, category, reliability, content_updated_on, retrieved_on, last_checked_on, notes)`
- `entities(id PK, type, canonical_name, normalized_name, locale, description, level_text, verification, version_scope, origin, updated_at)` — index `(type, normalized_name)`
- `entity_aliases(entity_id FK, alias, normalized, locale, origin, PK(entity_id, normalized))` — index `normalized`
- `entity_attributes(entity_id FK, key, value, PK)`; `entity_sources(entity_id FK, source_id, PK)`
- `entity_relations(from_id FK, type, to_id FK, PK(from_id, type, to_id))` — index `to_id`
- `mechanics(id PK, entity_id FK, type, title, summary, verification, expected_state_change, expected_result, vulnerability_duration, turn_conditions, version_scope, last_verified_on, origin, updated_at)`
- `mechanic_prerequisites(mechanic_id FK, ord, kind, description, entity_id, state, number)`
- `mechanic_steps(mechanic_id FK, seq_index, seq_label, step_order, action, target, position, required_state, when_kind, when_description, when_entity_id, when_state, when_number, rationale, verification)`
- `mechanic_notes(mechanic_id FK, ord, kind, text)`; `mechanic_unknowns(mechanic_id FK, ord, text)`
- `mechanic_sources(mechanic_id FK, ord, source_id FK, quote, locator)` — several quotes may cite the same source
- `observations(id PK, timestamp, method, ocr_text, region, candidates JSON, confirmation, confirmed_entity_id, screenshot_path)` — only written when history is enabled
- `import_log(id, package_id, package_name, imported_at, added, updated, conflicts_applied, skipped, content_hash)`

All child tables cascade on delete. Entity and mechanic rows are written with `INSERT … ON CONFLICT DO
UPDATE` (never `INSERT OR REPLACE`, which would cascade-delete children).

## Robustness

- Startup runs `PRAGMA quick_check`; a non-database or damaged file raises `DatabaseCorruptException`. The app
  offers to move it aside (`knowledge.db.corrupt-<timestamp>`, kept) and create a fresh one (seed re-applied).
- A database newer than the app (`user_version` > supported) is refused without modification.
- Failed migrations roll back and leave the previous version intact.
