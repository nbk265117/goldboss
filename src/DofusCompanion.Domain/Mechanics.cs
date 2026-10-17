namespace DofusCompanion.Domain;

public enum ConditionKind
{
    /// <summary>Cannot be observed automatically; the player must check it.</summary>
    Manual = 0,
    /// <summary>A given entity must be present in the fight.</summary>
    EntityPresent,
    /// <summary>A given entity must no longer be present (e.g. killed).</summary>
    EntityAbsent,
    /// <summary>A named state must be active on the boss/target.</summary>
    StateActive,
    /// <summary>Current turn number must be greater than or equal to Number.</summary>
    TurnAtLeast,
    /// <summary>Current turn number must be less than or equal to Number.</summary>
    TurnAtMost,
    /// <summary>Current turn number must be even.</summary>
    TurnEven,
    /// <summary>Current turn number must be odd.</summary>
    TurnOdd,
}

/// <summary>A structured, evaluable condition with a human-readable description.</summary>
public sealed record Condition
{
    public ConditionKind Kind { get; init; } = ConditionKind.Manual;
    public required string Description { get; init; }
    public string? EntityId { get; init; }
    public string? State { get; init; }
    public int? Number { get; init; }
}

public sealed record MechanicStep
{
    public required int Order { get; init; }
    /// <summary>What the player should do (manually, in game).</summary>
    public required string Action { get; init; }
    public string? Target { get; init; }
    public string? Position { get; init; }
    public string? RequiredState { get; init; }
    /// <summary>If set, the step only applies when this condition holds (conditional branch).</summary>
    public Condition? When { get; init; }
    /// <summary>Why the step matters (detailed mode).</summary>
    public string? Rationale { get; init; }
    /// <summary>Null means "inherits the mechanic's verification status".</summary>
    public VerificationStatus? Verification { get; init; }
}

/// <summary>One ordered way to perform a mechanic. A mechanic may have several valid alternatives.</summary>
public sealed record ActionSequence
{
    public string? Label { get; init; }
    public IReadOnlyList<MechanicStep> Steps { get; init; } = [];
}

public sealed record MechanicNote(NoteKind Kind, string Text);

public sealed record SourceReference(string SourceId, string? Quote = null, string? Locator = null);

/// <summary>
/// A structured rule describing how a boss/monster mechanic works. Human-readable guidance is generated
/// from this structure by the mechanics engine; no boss-specific text lives in code.
/// </summary>
public sealed record Mechanic
{
    public required string Id { get; init; }
    public required string EntityId { get; init; }
    public MechanicType Type { get; init; }
    public required string Title { get; init; }
    public string? Summary { get; init; }
    public VerificationStatus Verification { get; init; } = VerificationStatus.Unknown;
    public IReadOnlyList<Condition> Prerequisites { get; init; } = [];
    public IReadOnlyList<ActionSequence> Sequences { get; init; } = [];
    public string? ExpectedStateChange { get; init; }
    public string? ExpectedResult { get; init; }
    public string? VulnerabilityDuration { get; init; }
    public string? TurnConditions { get; init; }
    public IReadOnlyList<MechanicNote> Notes { get; init; } = [];
    /// <summary>Explicitly documented gaps: aspects of the mechanic no checked source covers.</summary>
    public IReadOnlyList<string> UnknownAspects { get; init; } = [];
    public string? VersionScope { get; init; }
    public DateOnly? LastVerifiedOn { get; init; }
    public IReadOnlyList<SourceReference> Sources { get; init; } = [];
    public RecordOrigin Origin { get; init; } = RecordOrigin.Seed;
}
