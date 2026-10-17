# Mechanics engine

Code: `Application/Mechanics/MechanicsEngine.cs`, models in `GuidanceModels.cs`; presentation in
`App/ViewModels/GuidancePresentation.cs`. The engine **explains**; it never executes anything.

## Input

A structured `Mechanic` (see [domain-model.md](domain-model.md)), its sources, the current `GameVersion`
baseline, and a `GuidanceContext` containing only what was observed:

- `TurnNumber` — typed by the user in the overlay/main window (empty = unknown);
- `ObservedEntityIds` — entities confirmed by recognition or by the user;
- `RosterIsComplete` — the user states that every monster present was identified;
- `ObservedStates` — states read on screen (reserved for future recognition).

## Condition evaluation (three-valued)

| Kind | Satisfied | Not satisfied | Unknown |
|---|---|---|---|
| EntityPresent | entity observed | roster complete and entity absent | otherwise |
| EntityAbsent | roster complete and entity not observed | entity observed | otherwise |
| StateActive | state observed | — (absence of observation proves nothing) | otherwise |
| TurnAtLeast / TurnAtMost / TurnEven / TurnOdd | turn known and test passes | turn known and test fails | turn unknown |
| Manual | — | — | always: "to check" |

Nothing unobserved is assumed.

## Output (`MechanicGuidance`)

- evaluated prerequisites;
- every alternative sequence, steps ordered and renumbered, each conditional step with its evaluated
  applicability, each step's effective verification (a step can never be *more* certain than its mechanic);
- cited sources with quotes;
- **gaps**: documented `UnknownAspects`, plus generated ones for unlock-type rules without steps, expected
  result or vulnerability duration, and for mechanics without source;
- **staleness**: never verified; not re-checked for > 365 days; every dated source predates the reference
  game version (DOFUS 3.0, 2024-12-03).

`ExplainEntity` sorts invulnerability/unlock rules first, then phases, then the rest, and reports
`NoMechanicDocumented` when there is nothing — the UI then says so explicitly.

## Presentation

Compact mode (overlay) prints an ordered block — OBJECTIVE, STATUS, PREREQUISITE (with evaluation), STEP n,
RESULT, DURATION, WARNING (failure cases), UNKNOWN, RECHECK, SOURCE — mirroring the brief's template; it
shows the first sequence and states how many alternatives exist. Detailed mode shows every field, the
evidence quote, rationale, per-step verification warnings and related entities. Status is always written as
text, not conveyed by colour alone.
