# Domain model

All records are immutable C# records in `DofusCompanion.Domain`. Unknown values are `null`, never invented
defaults. Ids are stable strings (`boss.kralamoure-geant`, `dungeon.canopee-du-kimbo`, `area.<slug>`,
`src.dpln.<slug>`, `mech.<boss>.<short>`); DofusDB imports use `monster.dofusdb-<gameId>` etc.

## Entities

`GameEntity` is the single identity abstraction for every named thing: `Type` ∈ Monster, Boss, Dungeon, Area,
Map, Spell, MonsterFamily, CharacterClass, Quest, Achievement. Fields: canonical name + locale, aliases
(with locale and origin), description, `LevelText` (as stated by the source, e.g. "190" or "18-22"),
`Attributes` (free key/value for sourced characteristics — keys used today: `resistances`, `category`,
`gameId`, `minLevel`, `optimalPlayerLevel`), verification status, version scope, source ids, origin.

The specific concepts of the brief map onto it as follows:

| Brief concept | Representation |
|---|---|
| Monster / Boss | `GameEntity` with Type Monster/Boss; boss rules are `Mechanic`s attached to it |
| Dungeon, Area/Map | `GameEntity` Dungeon / Area / Map; hierarchy through `EntityRelation` |
| Monster family | `GameEntity` MonsterFamily + `MemberOfFamily` relation |
| Spell and effect | `GameEntity` Spell (+ `CastsSpell` relation) and `Mechanic` of type SpellEffect |
| Associated dungeon/area, summons | `EntityRelation`: LocatedIn, PartOf, MemberOfFamily, Summons, CastsSpell |

## Mechanic (structured rule)

| Field | Meaning |
|---|---|
| `Type` | Invulnerability, Unlock, SpecialRule, Phase, Summon, Positioning, SpellEffect, Resistance, Other |
| `Prerequisites` | `Condition`s that must hold |
| `Sequences` | one or more **alternative** ordered `ActionSequence`s (e.g. Kimbo: Earth/Water or Air/Fire) |
| `MechanicStep` | order, action, target, position, required state, optional `When` condition (conditional branch), rationale, own verification (cannot exceed the mechanic's) |
| `ExpectedStateChange`, `ExpectedResult`, `VulnerabilityDuration`, `TurnConditions` | free text as sourced |
| `Notes` | Warning, CommonMistake, FailureCase, Exception, Restriction |
| `UnknownAspects` | explicitly documented gaps |
| `VersionScope`, `LastVerifiedOn` | validity statement and last check date |
| `Sources` | one or more `SourceReference(sourceId, quote, locator)` — at least one is mandatory |

`Condition.Kind`: Manual (player must check), EntityPresent, EntityAbsent, StateActive, TurnAtLeast,
TurnAtMost, TurnEven, TurnOdd.

## Three separate confidence notions (never merged)

1. **Recognition score** (`RecognizedCandidate.Score`, 0–1): normalized edit similarity between OCR text and
   a known name. 1.0 = identical after normalization. *Not a probability.*
2. **Source reliability** (`KnowledgeSource.Reliability`): Official, ReputableGuide, Community, UserSubmitted, Unknown.
3. **Mechanic verification** (`VerificationStatus`): VERIFIED, COMMUNITY_REPORTED, INFERRED, UNKNOWN.

## Other records

`KnowledgeSource` (title, URL, publisher, category, reliability, the source's own "last updated" date,
retrieved/checked dates, notes), `GameVersion` (staleness baseline: DOFUS 3.0, 2024-12-03),
`RecognitionObservation` (stored only if the user enables history; screenshot path only if enabled).

`RecordOrigin` (Seed, Import, User) drives conflict rules: user-edited and verified records are never
overwritten silently.
