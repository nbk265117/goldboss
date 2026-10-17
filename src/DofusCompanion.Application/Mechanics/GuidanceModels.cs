using DofusCompanion.Domain;

namespace DofusCompanion.Application.Mechanics;

/// <summary>
/// What the companion has actually observed about the current fight. Everything not listed here is unknown:
/// the engine never assumes hidden game state.
/// </summary>
public sealed record GuidanceContext
{
    /// <summary>Current turn, if the user entered it or it was read from the screen.</summary>
    public int? TurnNumber { get; init; }
    /// <summary>Entities seen in the fight (recognized and confirmed).</summary>
    public IReadOnlySet<string> ObservedEntityIds { get; init; } = new HashSet<string>();
    /// <summary>
    /// True only if the user confirmed that <see cref="ObservedEntityIds"/> is the complete list of entities in
    /// the fight. Without it, absence of an entity cannot be concluded.
    /// </summary>
    public bool RosterIsComplete { get; init; }
    /// <summary>States observed as active (e.g. read from a tooltip).</summary>
    public IReadOnlySet<string> ObservedStates { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static GuidanceContext Empty { get; } = new();
}

public enum ConditionResult
{
    /// <summary>Cannot be decided from observed evidence: the player must check.</summary>
    Unknown,
    Satisfied,
    NotSatisfied,
}

public sealed record EvaluatedCondition(Condition Condition, ConditionResult Result);

public sealed record GuidanceStep
{
    public required int Number { get; init; }
    public required MechanicStep Source { get; init; }
    public EvaluatedCondition? Applicability { get; init; }
    /// <summary>The step's own status, or the mechanic's when the step does not override it.</summary>
    public required VerificationStatus Verification { get; init; }
}

public sealed record GuidanceSequence(string? Label, IReadOnlyList<GuidanceStep> Steps);

public sealed record CitedSource(KnowledgeSource Source, string? Quote, string? Locator);

public enum StalenessReason
{
    /// <summary>The source content predates the current game version baseline.</summary>
    SourcePredatesGameVersion,
    /// <summary>Nobody re-checked the record for longer than the configured period.</summary>
    NotRecentlyVerified,
    /// <summary>The record has no verification date at all.</summary>
    NeverVerified,
}

public sealed record StalenessWarning(StalenessReason Reason, string Detail);

public enum GapKind
{
    /// <summary>Explicitly listed by the knowledge record.</summary>
    Documented,
    NoActionSequence,
    NoExpectedResult,
    NoVulnerabilityDuration,
    NoSource,
}

public sealed record KnowledgeGap(GapKind Kind, string? Text = null);

/// <summary>Everything needed to present one mechanic: structured, presentation-neutral.</summary>
public sealed record MechanicGuidance
{
    public required Mechanic Mechanic { get; init; }
    public required IReadOnlyList<EvaluatedCondition> Prerequisites { get; init; }
    public required IReadOnlyList<GuidanceSequence> Sequences { get; init; }
    public required IReadOnlyList<CitedSource> Sources { get; init; }
    public required IReadOnlyList<KnowledgeGap> Gaps { get; init; }
    public required IReadOnlyList<StalenessWarning> Staleness { get; init; }

    public bool AllPrerequisitesSatisfied => Prerequisites.All(p => p.Result == ConditionResult.Satisfied);
    public bool AnyPrerequisiteFailed => Prerequisites.Any(p => p.Result == ConditionResult.NotSatisfied);
    public bool HasUnverifiedSteps => Sequences.SelectMany(s => s.Steps).Any(s => s.Verification != VerificationStatus.Verified);
    public bool IsEstablishedFact => Mechanic.Verification == VerificationStatus.Verified;
}

/// <summary>Guidance for one entity: its context and all its mechanics, or an explicit "nothing known".</summary>
public sealed record EntityGuidance
{
    public required GameEntity Entity { get; init; }
    /// <summary>Dungeon/area the entity is located in, when known.</summary>
    public IReadOnlyList<GameEntity> Locations { get; init; } = [];
    public IReadOnlyList<MechanicGuidance> Mechanics { get; init; } = [];
    /// <summary>For a dungeon: guidance of the bosses located in it (their mechanics are what players look for).</summary>
    public IReadOnlyList<EntityGuidance> ContainedBosses { get; init; } = [];
    public bool NoMechanicDocumented => Mechanics.Count == 0 && ContainedBosses.All(b => b.Mechanics.Count == 0);
}
