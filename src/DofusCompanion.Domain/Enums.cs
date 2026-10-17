namespace DofusCompanion.Domain;

/// <summary>Kind of game entity stored in the knowledge base.</summary>
public enum EntityType
{
    Monster,
    Boss,
    Dungeon,
    Area,
    Map,
    Spell,
    MonsterFamily,
    CharacterClass,
    Quest,
    Achievement,
}

/// <summary>
/// Provenance state of a piece of knowledge. This is NOT a probability: it states what kind of
/// evidence backs the record.
/// </summary>
public enum VerificationStatus
{
    /// <summary>Insufficient information.</summary>
    Unknown = 0,
    /// <summary>Inferred from incomplete evidence. Never presented as established fact.</summary>
    Inferred = 1,
    /// <summary>Reported by a community source, not independently confirmed.</summary>
    CommunityReported = 2,
    /// <summary>Supported by a reliable source that has been checked.</summary>
    Verified = 3,
}

/// <summary>Categorical reliability of a knowledge source (kept separate from recognition and verification).</summary>
public enum SourceReliability
{
    Unknown = 0,
    UserSubmitted = 1,
    Community = 2,
    ReputableGuide = 3,
    Official = 4,
}

public enum SourceCategory
{
    Other = 0,
    Official,
    Guide,
    Wiki,
    Forum,
    User,
}

public enum MechanicType
{
    Other = 0,
    Invulnerability,
    Unlock,
    SpecialRule,
    Phase,
    Summon,
    Positioning,
    SpellEffect,
    Resistance,
}

public enum RelationType
{
    /// <summary>Monster/boss/map is located in a dungeon or area.</summary>
    LocatedIn,
    /// <summary>Dungeon/map belongs to an area or region.</summary>
    PartOf,
    /// <summary>Monster belongs to a monster family.</summary>
    MemberOfFamily,
    /// <summary>Entity summons another entity.</summary>
    Summons,
    /// <summary>Entity casts a spell.</summary>
    CastsSpell,
}

/// <summary>Where a record came from; used for conflict resolution (verified records are never silently overwritten).</summary>
public enum RecordOrigin
{
    Seed,
    Import,
    User,
}

public enum NoteKind
{
    Warning,
    CommonMistake,
    FailureCase,
    Exception,
    Restriction,
}
