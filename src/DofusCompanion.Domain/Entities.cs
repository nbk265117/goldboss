namespace DofusCompanion.Domain;

/// <summary>A searchable alias of an entity (localized name, alternate spelling, user-confirmed OCR variant).</summary>
public sealed record EntityAlias(string Text, string? Locale = null, RecordOrigin Origin = RecordOrigin.Seed);

public sealed record EntityRelation(string FromId, RelationType Type, string ToId);

/// <summary>
/// Common identity abstraction for every named game entity (monster, boss, dungeon, area, spell...).
/// Unknown values are null rather than invented defaults.
/// </summary>
public sealed record GameEntity
{
    /// <summary>Stable identifier, e.g. "boss.kralamoure-geant". Never reused for another entity.</summary>
    public required string Id { get; init; }
    public required EntityType Type { get; init; }
    public required string CanonicalName { get; init; }
    public string Locale { get; init; } = "fr";
    public string? Description { get; init; }
    public IReadOnlyList<EntityAlias> Aliases { get; init; } = [];
    /// <summary>Level or level range as stated by the source (free text, e.g. "190" or "110-130").</summary>
    public string? LevelText { get; init; }
    /// <summary>Sourced characteristics (resistances, range, cooldown...). Keys are documented in docs/domain-model.md.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
    public VerificationStatus Verification { get; init; } = VerificationStatus.Unknown;
    public string? VersionScope { get; init; }
    public IReadOnlyList<string> SourceIds { get; init; } = [];
    public RecordOrigin Origin { get; init; } = RecordOrigin.Seed;

    /// <summary>All names under which the entity can be found, canonical first.</summary>
    public IEnumerable<string> AllNames()
    {
        yield return CanonicalName;
        foreach (var a in Aliases)
            yield return a.Text;
    }
}

public sealed record KnowledgeSource
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Url { get; init; }
    public string? Publisher { get; init; }
    public SourceCategory Category { get; init; }
    public SourceReliability Reliability { get; init; }
    /// <summary>"Last updated" date printed by the source itself, when it shows one. Used for staleness checks.</summary>
    public DateOnly? ContentUpdatedOn { get; init; }
    /// <summary>Date the content was retrieved for this knowledge base.</summary>
    public DateOnly? RetrievedOn { get; init; }
    /// <summary>Date a person last checked the record against the source.</summary>
    public DateOnly? LastCheckedOn { get; init; }
    public string? Notes { get; init; }
}

/// <summary>A game release used as a staleness baseline (e.g. the Dofus 3 / Unity launch).</summary>
public sealed record GameVersion
{
    public required string Id { get; init; }
    public string? Version { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public string? SourceId { get; init; }
    public DateOnly? VerifiedOn { get; init; }
    public string? Notes { get; init; }
}
