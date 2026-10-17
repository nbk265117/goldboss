# Architecture

Local Windows desktop application, .NET 10 (LTS), WPF + MVVM. No server, no cloud, no container.

```
src/
  DofusCompanion.Domain          Records and enums only (GameEntity, Mechanic, Condition, KnowledgeSource…). No dependencies.
  DofusCompanion.Application     Pure logic, testable without Windows: text normalization & fuzzy matching,
                                 mechanics engine, recognition orchestration, image preprocessing,
                                 import planning (conflicts), search service, settings model.
                                 Defines the integration boundaries: IKnowledgeRepository, IOcrProvider, IImageDecoder.
  DofusCompanion.Infrastructure  SQLite repository + migrations, JSON package serializer + schema validation,
                                 import/seed service, JSON settings store, file logger, local paths.
  DofusCompanion.Ocr.Windows     IOcrProvider over Windows.Media.Ocr, IImageDecoder over WIC (WinRT projections).
  DofusCompanion.App             WPF shell: views, view models, overlay, global hotkeys, screen capture, localization.
tools/
  DofusCompanion.DofusDbImporter User-run console tool producing a knowledge package from DofusDB. Not used by the app.
tests/
  DofusCompanion.Core.Tests            Domain/Application unit tests (net10.0)
  DofusCompanion.Infrastructure.Tests  SQLite/import/export/search/settings/importer integration tests (net10.0)
  DofusCompanion.Ocr.Tests             Windows OCR + full recognition pipeline on image fixtures (net10.0-windows)
data/
  schemas/knowledge-package.schema.json   Versioned import/export format (JSON Schema draft-07)
  seed/*.dofus-knowledge.json             Verified bundled knowledge (embedded into Infrastructure)
  imports/example-kimbo.dofus-knowledge.json
scripts/  build.ps1, test.ps1, publish.ps1, ui-smoke.ps1, generate-ocr-fixtures.ps1, check-localization.py
```

The suggested structure had ten production projects; separate *Vision*, *Overlay*, *Knowledge* and *Settings*
projects would have been empty shells, so their responsibilities live in the five projects above
(preprocessing in Application/Imaging, overlay in App, knowledge in Application/Knowledge + Infrastructure/Knowledge,
settings model in Application/Settings with its store in Infrastructure).

## Dependency direction

```
App ──> Infrastructure ──> Application ──> Domain
 └────> Ocr.Windows ─────> Application
```

Domain and Application have no UI, OCR-vendor or database dependency. Replacing the OCR engine means writing
another `IOcrProvider`; replacing SQLite means another `IKnowledgeRepository`. The mechanics engine never sees
recognition code, and recognition never sees mechanics.

## Main flows

**Search** — `KnowledgeService` keeps an immutable in-memory snapshot (entities, normalized names, relations,
mechanics, sources, the `EntityMatcher`), rebuilt after any write (`Invalidate`). Ranking: exact > prefix >
word prefix > contains > bounded fuzzy distance > mechanic-text hit.

**Guidance** — `KnowledgeService.GetGuidanceAsync(entityId, GuidanceContext)` → `MechanicsEngine.ExplainEntity`
evaluates prerequisites and conditional steps against what was *observed* (turn number entered by the user,
confirmed entities, whether the roster is declared complete) and reports gaps and staleness. The App's
`GuidancePresenter` turns that into display cards; all wording comes from localization tables, no
boss-specific text exists in code.

**Recognition** — image (file / clipboard / opt-in capture) → `RecognitionService`: optional crop + padding →
preprocessing variants (original, upscale+contrast, inverted+threshold, sharpen) → `IOcrProvider` →
`EntityMatcher.FindInText` (token windows over each OCR line, trigram pre-filter, normalized edit
similarity) → ranked candidates with evidence → outcome (Confident / NeedsConfirmation / Ambiguous / NoMatch).
Stops at the first confident variant.

**Import** — `KnowledgePackageSerializer.ReadAsync` (size limit → JSON Schema → semantic checks) →
`ImportPlanner.Plan` (pure; decides Add / Update / Unchanged / Conflict / Skip) → user reviews and resolves
conflicts in the UI → `SqliteKnowledgeRepository.ApplyImportAsync` (single transaction, import log).

## Threading

All database and OCR work is asynchronous; SQLite calls run on the thread pool (`Task.Run`), pixel work is
offloaded, OCR uses WinRT async APIs with a 30 s timeout. Search input is debounced (200 ms main window,
150 ms overlay) and cancellable. There is no polling loop: capture happens only on click or hotkey.

## Key decisions

| Decision | Reason |
|---|---|
| Microsoft.Data.Sqlite + hand-written SQL migrations (`PRAGMA user_version`) | Small fixed schema; no EF tooling needed; migrations are explicit and testable (v1→v2 test). |
| Own fuzzy matching (OSA distance, token sort, trigram index) | Simple, dependency-free, measurable; scores have a defined meaning. |
| NJsonSchema (MIT) instead of JsonSchema.Net 9 | JsonSchema.Net 9.x moved to a non-OSI "maintenance fee" EULA. |
| Windows.Media.Ocr | Local, free, already on Windows, French available; replaceable behind `IOcrProvider`. |
| GDI BitBlt one-shot capture | Same mechanism as Print Screen, user-triggered, no game-process access. |
| Localization via embedded JSON + `{l:T Key}` markup extension | Live language switch, no resx designer tooling, keys checked by script. |
| Self-contained publish with ReadyToRun | Runs without installing .NET; warm start 1.6–2.2 s measured. |
