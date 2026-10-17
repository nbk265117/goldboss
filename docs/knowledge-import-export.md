# Knowledge import, export and contribution

## Format

JSON, described by `data/schemas/knowledge-package.schema.json` (draft-07, `formatVersion: 1`). Top level:
`packageId`, `name`, `createdOn`, optional `description`, `author`, `testOnly`, `gameVersions`, and the arrays
`sources`, `entities` (with `aliases`, `attributes`, `relations`), `mechanics` (with `prerequisites`,
`sequences[].steps[]`, `notes`, `unknownAspects`, `sources[]` — **at least one source per mechanic**).
Verification values: `VERIFIED`, `COMMUNITY_REPORTED`, `INFERRED`, `UNKNOWN`.
Example: `data/imports/example-kimbo.dofus-knowledge.json`.

## Import (Knowledge tab › Import a package…)

1. **Validation** before anything else: `.json` extension, ≤ 50 MB, JSON Schema (unknown properties,
   bad ids/dates/enums, mechanics without sources are rejected), then semantic checks (duplicate ids, duplicate
   step orders, invalid dates). An invalid package changes nothing and the errors are listed.
2. **Plan / preview** (`ImportPlanner`, pure function):
   - *Add* — new id; *Unchanged* — identical content (origin ignored);
   - *Update* — existing record is unverified, not user-edited, and incoming provenance is equal or better;
   - *Conflict* — existing record is VERIFIED, or was edited by you, or incoming provenance is weaker, or a
     source/game-version differs. Default resolution is always **keep existing**; you may choose Replace or
     (entities) Merge;
   - *Possible duplicate* — same type and normalized name as an existing entity under another id: default
     **merge** (aliases, missing attributes/level, relations are added; nothing existing is overwritten) and the
     incoming id is re-pointed to the existing one for its mechanics/relations;
   - *Skip* — mechanic whose entity or source does not exist.
   Imports only ever **add** relations; a partial package never deletes relations it does not mention.
   Test-only packages (`testOnly: true`) are refused.
3. **Apply** in one SQLite transaction, logged in `import_log`. Package content is data only: nothing in it
   is executed, and it contains no file paths.

Bundled seed packages are applied automatically at startup when their content hash changes; records
previously installed by a seed are updated, records you edited are kept.

## Export

*Export everything* or *Export* on an entity: the entity, its mechanics, the sources they cite and its
relation targets, written atomically (temp file + move) to the path you choose. Re-importing an export into
the same database changes nothing.

## Contributing verified mechanics

1. One mechanic = one structured rule, in your own words, faithful to the source; ordered steps; target /
   position / state when the source states them; failure cases and exceptions as notes; anything the source
   does not state goes to `unknownAspects` — never fill gaps with general game knowledge.
2. Cite the page (`sources`) and add a **short verbatim quote** (≤ 300 characters) that supports the core
   of the rule; record the page's own "last updated" date (`contentUpdatedOn`).
3. `VERIFIED` only if the page states the rule clearly and you checked it; `COMMUNITY_REPORTED` if the page is
   vague, contradictory or marked as needing revision; `INFERRED` only for your own deductions.
4. Set `versionScope` ("Dofus 3 (guide mis à jour le …)" or "Antérieur à Dofus 3 — à revérifier").

The bundled data was produced exactly this way: pages fetched one at a time (≥ 3 s apart, sites whose
robots.txt allows it), converted to text, and every quote checked with an exact substring search.

## DofusDB importer (user-run)

`tools/DofusCompanion.DofusDbImporter` builds a package of **entities only** (monsters, bosses, dungeons,
areas/dimensions, families, localized names as aliases, level ranges) from the DofusDB public API.

```powershell
dotnet run --project tools/DofusCompanion.DofusDbImporter -- --accept-terms --out dofusdb.dofus-knowledge.json
```

- Refuses network access without `--accept-terms`: check DofusDB's conditions first (its robots.txt excludes
  AI agents, so it was **never run against the live site during development**; it is tested only against
  synthetic responses) and remember Ankama's CGU §13 restricts reuse of game data. Personal use only.
- Sequential requests, ≥ 1 s apart (1.5 s default), request cap (600), honours HTTP 429 `Retry-After`,
  stops on 401/403, caches every page (`dofusdb-cache/`) so re-runs don't hit the site; `--offline` uses the
  cache only.
- Output is validated with the application's schema before being written. Records are marked
  `COMMUNITY_REPORTED` with a DofusDB source. On import, DofusDB bosses that already exist (e.g. *Kimbo*) are
  merged into the verified entries (provenance kept, missing level filled).
- The exact field names of DofusDB's monster/area endpoints could not be confirmed during development; the
  mapper reads them defensively and reports skipped records. Inspect the first run's warnings.
