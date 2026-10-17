using DofusCompanion.Application.Matching;
using DofusCompanion.Domain;

namespace DofusCompanion.Application.Mechanics;

public sealed record StalenessPolicy
{
    /// <summary>Records not re-checked for longer than this are flagged.</summary>
    public TimeSpan MaxAgeSinceVerification { get; init; } = TimeSpan.FromDays(365);
}

/// <summary>
/// Turns structured mechanic rules into ordered, evaluated guidance. The engine only explains: it never
/// performs any in-game action, and it never fills gaps with generic knowledge. Gaps are reported.
/// </summary>
public sealed class MechanicsEngine
{
    private readonly StalenessPolicy _policy;
    private readonly TimeProvider _time;

    public MechanicsEngine(StalenessPolicy? policy = null, TimeProvider? time = null)
    {
        _policy = policy ?? new StalenessPolicy();
        _time = time ?? TimeProvider.System;
    }

    public static ConditionResult Evaluate(Condition condition, GuidanceContext context)
    {
        switch (condition.Kind)
        {
            case ConditionKind.EntityPresent when condition.EntityId is { } present:
                if (context.ObservedEntityIds.Contains(present)) return ConditionResult.Satisfied;
                return context.RosterIsComplete ? ConditionResult.NotSatisfied : ConditionResult.Unknown;

            case ConditionKind.EntityAbsent when condition.EntityId is { } absent:
                if (context.ObservedEntityIds.Contains(absent)) return ConditionResult.NotSatisfied;
                return context.RosterIsComplete ? ConditionResult.Satisfied : ConditionResult.Unknown;

            case ConditionKind.StateActive when condition.State is { } state:
                // Absence of an observed state does not prove the state is inactive.
                return context.ObservedStates.Contains(state) ? ConditionResult.Satisfied : ConditionResult.Unknown;

            case ConditionKind.TurnAtLeast when condition.Number is { } min:
                return Turn(context, t => t >= min);
            case ConditionKind.TurnAtMost when condition.Number is { } max:
                return Turn(context, t => t <= max);
            case ConditionKind.TurnEven:
                return Turn(context, t => t % 2 == 0);
            case ConditionKind.TurnOdd:
                return Turn(context, t => t % 2 == 1);

            default:
                // Manual conditions, or structured conditions missing their parameter.
                return ConditionResult.Unknown;
        }

        static ConditionResult Turn(GuidanceContext c, Func<int, bool> test) =>
            c.TurnNumber is { } t ? (test(t) ? ConditionResult.Satisfied : ConditionResult.NotSatisfied) : ConditionResult.Unknown;
    }

    public MechanicGuidance Explain(Mechanic mechanic, IReadOnlyDictionary<string, KnowledgeSource> sources,
        GuidanceContext? context = null, GameVersion? currentVersion = null)
    {
        context ??= GuidanceContext.Empty;

        var prerequisites = mechanic.Prerequisites
            .Select(c => new EvaluatedCondition(c, Evaluate(c, context)))
            .ToList();

        var sequences = mechanic.Sequences
            .Select(seq => new GuidanceSequence(
                seq.Label,
                seq.Steps
                    .OrderBy(s => s.Order)
                    .Select((s, i) => new GuidanceStep
                    {
                        Number = i + 1,
                        Source = s,
                        Applicability = s.When is null ? null : new EvaluatedCondition(s.When, Evaluate(s.When, context)),
                        // A step can never claim more certainty than its mechanic.
                        Verification = s.Verification is { } v && v < mechanic.Verification ? v : mechanic.Verification,
                    })
                    .ToList()))
            .ToList();

        var cited = mechanic.Sources
            .Where(r => sources.ContainsKey(r.SourceId))
            .Select(r => new CitedSource(sources[r.SourceId], r.Quote, r.Locator))
            .ToList();

        return new MechanicGuidance
        {
            Mechanic = mechanic,
            Prerequisites = prerequisites,
            Sequences = sequences,
            Sources = cited,
            Gaps = FindGaps(mechanic, cited),
            Staleness = FindStaleness(mechanic, cited, currentVersion),
        };
    }

    public EntityGuidance ExplainEntity(GameEntity entity, IEnumerable<Mechanic> mechanics,
        IReadOnlyDictionary<string, KnowledgeSource> sources, IReadOnlyList<GameEntity> locations,
        GuidanceContext? context = null, GameVersion? currentVersion = null)
    {
        var ordered = mechanics
            // Unlock/invulnerability rules first: they are what the player needs before anything else.
            .OrderBy(m => m.Type switch
            {
                MechanicType.Invulnerability or MechanicType.Unlock => 0,
                MechanicType.Phase => 1,
                _ => 2,
            })
            .ThenByDescending(m => m.Verification)
            .ThenBy(m => m.Title, StringComparer.CurrentCulture)
            .Select(m => Explain(m, sources, context, currentVersion))
            .ToList();

        return new EntityGuidance { Entity = entity, Locations = locations, Mechanics = ordered };
    }

    private static List<KnowledgeGap> FindGaps(Mechanic m, IReadOnlyList<CitedSource> cited)
    {
        var gaps = m.UnknownAspects.Select(u => new KnowledgeGap(GapKind.Documented, u)).ToList();

        var isUnlockLike = m.Type is MechanicType.Invulnerability or MechanicType.Unlock;
        if (isUnlockLike && m.Sequences.All(s => s.Steps.Count == 0))
            gaps.Add(new KnowledgeGap(GapKind.NoActionSequence));
        if (isUnlockLike && string.IsNullOrWhiteSpace(m.ExpectedResult))
            gaps.Add(new KnowledgeGap(GapKind.NoExpectedResult));
        // A documented gap about the duration is more precise than the generic one: do not say it twice.
        var durationDocumented = m.UnknownAspects.Any(u => TextNormalizer.Normalize(u) is var n && (n.Contains("duree", StringComparison.Ordinal) || n.Contains("duration", StringComparison.Ordinal)));
        if (isUnlockLike && string.IsNullOrWhiteSpace(m.VulnerabilityDuration) && !durationDocumented)
            gaps.Add(new KnowledgeGap(GapKind.NoVulnerabilityDuration));
        if (cited.Count == 0)
            gaps.Add(new KnowledgeGap(GapKind.NoSource));

        return gaps;
    }

    private List<StalenessWarning> FindStaleness(Mechanic m, IReadOnlyList<CitedSource> cited, GameVersion? currentVersion)
    {
        var warnings = new List<StalenessWarning>();
        var today = DateOnly.FromDateTime(_time.GetLocalNow().Date);

        if (m.LastVerifiedOn is not { } verified)
            warnings.Add(new StalenessWarning(StalenessReason.NeverVerified, ""));
        else if (today.DayNumber - verified.DayNumber > _policy.MaxAgeSinceVerification.TotalDays)
            warnings.Add(new StalenessWarning(StalenessReason.NotRecentlyVerified, verified.ToString("yyyy-MM-dd")));

        if (currentVersion?.ReleaseDate is { } release)
        {
            // Only flag when every dated source predates the release: one up-to-date source is enough.
            var dated = cited.Where(c => c.Source.ContentUpdatedOn is not null).ToList();
            if (dated.Count > 0 && dated.All(c => c.Source.ContentUpdatedOn < release))
            {
                var newest = dated.Max(c => c.Source.ContentUpdatedOn!.Value);
                warnings.Add(new StalenessWarning(StalenessReason.SourcePredatesGameVersion,
                    $"{newest:yyyy-MM-dd} < {currentVersion.Version ?? currentVersion.Id} ({release:yyyy-MM-dd})"));
            }
        }

        return warnings;
    }
}
