using DofusCompanion.Application.Mechanics;
using DofusCompanion.Domain;

namespace DofusCompanion.Core.Tests;

public class MechanicsEngineTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static readonly MechanicsEngine Engine = new(time: new FixedTime(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero)));
    private static readonly Dictionary<string, KnowledgeSource> Sources = new() { ["src.test"] = TestData.Source(updated: new DateOnly(2026, 1, 1)) };

    [Theory]
    [InlineData(ConditionKind.TurnEven, null, 4, ConditionResult.Satisfied)]
    [InlineData(ConditionKind.TurnEven, null, 3, ConditionResult.NotSatisfied)]
    [InlineData(ConditionKind.TurnOdd, null, 3, ConditionResult.Satisfied)]
    [InlineData(ConditionKind.TurnAtLeast, 7, 7, ConditionResult.Satisfied)]
    [InlineData(ConditionKind.TurnAtLeast, 7, 6, ConditionResult.NotSatisfied)]
    [InlineData(ConditionKind.TurnAtMost, 4, 5, ConditionResult.NotSatisfied)]
    [InlineData(ConditionKind.TurnEven, null, null, ConditionResult.Unknown)]
    public void Turn_conditions(ConditionKind kind, int? number, int? turn, ConditionResult expected)
    {
        var c = new Condition { Kind = kind, Number = number, Description = "x" };
        Assert.Equal(expected, MechanicsEngine.Evaluate(c, new GuidanceContext { TurnNumber = turn }));
    }

    [Fact]
    public void Entity_absence_is_unknown_unless_roster_is_complete()
    {
        var c = new Condition { Kind = ConditionKind.EntityAbsent, EntityId = "mon.a", Description = "a is dead" };
        Assert.Equal(ConditionResult.Unknown, MechanicsEngine.Evaluate(c, GuidanceContext.Empty));
        Assert.Equal(ConditionResult.NotSatisfied, MechanicsEngine.Evaluate(c, new GuidanceContext { ObservedEntityIds = new HashSet<string> { "mon.a" } }));
        Assert.Equal(ConditionResult.Satisfied, MechanicsEngine.Evaluate(c, new GuidanceContext { RosterIsComplete = true }));
    }

    [Fact]
    public void Entity_presence_is_never_assumed()
    {
        var c = new Condition { Kind = ConditionKind.EntityPresent, EntityId = "mon.a", Description = "a present" };
        Assert.Equal(ConditionResult.Unknown, MechanicsEngine.Evaluate(c, GuidanceContext.Empty));
        Assert.Equal(ConditionResult.NotSatisfied, MechanicsEngine.Evaluate(c, new GuidanceContext { RosterIsComplete = true }));
    }

    [Fact]
    public void Unobserved_state_is_unknown_not_false()
    {
        var c = new Condition { Kind = ConditionKind.StateActive, State = "Pesanteur", Description = "x" };
        Assert.Equal(ConditionResult.Unknown, MechanicsEngine.Evaluate(c, GuidanceContext.Empty));
        Assert.Equal(ConditionResult.Satisfied, MechanicsEngine.Evaluate(c, new GuidanceContext { ObservedStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pesanteur" } }));
    }

    [Fact]
    public void Manual_conditions_are_always_unknown() =>
        Assert.Equal(ConditionResult.Unknown, MechanicsEngine.Evaluate(new Condition { Description = "check yourself" }, new GuidanceContext { TurnNumber = 2, RosterIsComplete = true }));

    [Fact]
    public void Steps_are_ordered_and_numbered()
    {
        var m = TestData.Mechanic() with
        {
            Sequences = [new ActionSequence { Steps = [new MechanicStep { Order = 20, Action = "second" }, new MechanicStep { Order = 10, Action = "first" }] }],
        };
        var g = Engine.Explain(m, Sources);
        Assert.Equal(["first", "second"], g.Sequences[0].Steps.Select(s => s.Source.Action));
        Assert.Equal([1, 2], g.Sequences[0].Steps.Select(s => s.Number));
    }

    [Fact]
    public void Alternative_sequences_are_kept_separate()
    {
        var m = TestData.Mechanic() with
        {
            Sequences =
            [
                new ActionSequence { Label = "A", Steps = [new MechanicStep { Order = 1, Action = "a1" }] },
                new ActionSequence { Label = "B", Steps = [new MechanicStep { Order = 1, Action = "b1" }] },
            ],
        };
        var g = Engine.Explain(m, Sources);
        Assert.Equal(["A", "B"], g.Sequences.Select(s => s.Label));
    }

    [Fact]
    public void Conditional_step_applicability_is_evaluated()
    {
        var m = TestData.Mechanic() with
        {
            Sequences = [new ActionSequence { Steps = [new MechanicStep { Order = 1, Action = "hit", When = new Condition { Kind = ConditionKind.TurnEven, Description = "even" } }] }],
        };
        Assert.Equal(ConditionResult.NotSatisfied, Engine.Explain(m, Sources, new GuidanceContext { TurnNumber = 3 }).Sequences[0].Steps[0].Applicability!.Result);
        Assert.Equal(ConditionResult.Satisfied, Engine.Explain(m, Sources, new GuidanceContext { TurnNumber = 4 }).Sequences[0].Steps[0].Applicability!.Result);
    }

    [Fact]
    public void Step_cannot_be_more_certain_than_its_mechanic()
    {
        var m = TestData.Mechanic(v: VerificationStatus.CommunityReported) with
        {
            Sequences = [new ActionSequence { Steps = [new MechanicStep { Order = 1, Action = "x", Verification = VerificationStatus.Verified }, new MechanicStep { Order = 2, Action = "y", Verification = VerificationStatus.Inferred }] }],
        };
        var steps = Engine.Explain(m, Sources).Sequences[0].Steps;
        Assert.Equal(VerificationStatus.CommunityReported, steps[0].Verification);
        Assert.Equal(VerificationStatus.Inferred, steps[1].Verification);
        Assert.True(Engine.Explain(m, Sources).HasUnverifiedSteps);
    }

    [Fact]
    public void Missing_information_is_reported_as_gaps()
    {
        var m = TestData.Mechanic() with { Sequences = [], ExpectedResult = null, VulnerabilityDuration = null, Sources = [], UnknownAspects = ["Placement exact inconnu"] };
        var gaps = Engine.Explain(m, Sources).Gaps.Select(g => g.Kind).ToList();
        Assert.Contains(GapKind.Documented, gaps);
        Assert.Contains(GapKind.NoActionSequence, gaps);
        Assert.Contains(GapKind.NoExpectedResult, gaps);
        Assert.Contains(GapKind.NoVulnerabilityDuration, gaps);
        Assert.Contains(GapKind.NoSource, gaps);
    }

    [Fact]
    public void Documented_duration_gap_is_not_repeated()
    {
        var m = TestData.Mechanic() with { VulnerabilityDuration = null, UnknownAspects = ["Durée de la vulnérabilité non précisée par la source."] };
        var gaps = Engine.Explain(m, Sources).Gaps;
        Assert.Single(gaps);
        Assert.Equal(GapKind.Documented, gaps[0].Kind);
    }

    [Fact]
    public void Complete_mechanic_has_no_generated_gaps() =>
        Assert.Empty(Engine.Explain(TestData.Mechanic(), Sources).Gaps);

    [Fact]
    public void Source_older_than_game_version_is_flagged_outdated()
    {
        var version = new GameVersion { Id = "v3", Version = "3.0", ReleaseDate = new DateOnly(2024, 12, 3) };
        var old = new Dictionary<string, KnowledgeSource> { ["src.test"] = TestData.Source(updated: new DateOnly(2020, 7, 20)) };
        var g = Engine.Explain(TestData.Mechanic(), old, currentVersion: version);
        Assert.Contains(g.Staleness, s => s.Reason == StalenessReason.SourcePredatesGameVersion);

        var fresh = Engine.Explain(TestData.Mechanic(), Sources, currentVersion: version);
        Assert.DoesNotContain(fresh.Staleness, s => s.Reason == StalenessReason.SourcePredatesGameVersion);
    }

    [Fact]
    public void Undated_source_is_not_flagged_as_predating()
    {
        var version = new GameVersion { Id = "v3", ReleaseDate = new DateOnly(2024, 12, 3) };
        var undated = new Dictionary<string, KnowledgeSource> { ["src.test"] = TestData.Source(updated: null) };
        Assert.DoesNotContain(Engine.Explain(TestData.Mechanic(), undated, currentVersion: version).Staleness,
            s => s.Reason == StalenessReason.SourcePredatesGameVersion);
    }

    [Fact]
    public void Old_verification_date_is_flagged()
    {
        var g = Engine.Explain(TestData.Mechanic() with { LastVerifiedOn = new DateOnly(2024, 1, 1) }, Sources);
        Assert.Contains(g.Staleness, s => s.Reason == StalenessReason.NotRecentlyVerified);
        Assert.Contains(Engine.Explain(TestData.Mechanic() with { LastVerifiedOn = null }, Sources).Staleness, s => s.Reason == StalenessReason.NeverVerified);
    }

    [Fact]
    public void Entity_without_mechanics_reports_nothing_documented()
    {
        var g = Engine.ExplainEntity(TestData.Entity("boss.x", "X"), [], Sources, []);
        Assert.True(g.NoMechanicDocumented);
    }

    [Fact]
    public void Unlock_mechanics_come_first()
    {
        var special = TestData.Mechanic("m.special") with { Type = MechanicType.SpecialRule, Title = "A" };
        var unlock = TestData.Mechanic("m.unlock") with { Type = MechanicType.Unlock, Title = "Z" };
        var g = Engine.ExplainEntity(TestData.Entity("boss.test", "T"), [special, unlock], Sources, []);
        Assert.Equal("m.unlock", g.Mechanics[0].Mechanic.Id);
    }

    [Fact]
    public void Prerequisite_summary_flags()
    {
        var m = TestData.Mechanic() with { Prerequisites = [new Condition { Kind = ConditionKind.TurnEven, Description = "even" }] };
        Assert.True(Engine.Explain(m, Sources, new GuidanceContext { TurnNumber = 2 }).AllPrerequisitesSatisfied);
        Assert.True(Engine.Explain(m, Sources, new GuidanceContext { TurnNumber = 3 }).AnyPrerequisiteFailed);
        var unknown = Engine.Explain(m, Sources);
        Assert.False(unknown.AllPrerequisitesSatisfied);
        Assert.False(unknown.AnyPrerequisiteFailed);
    }
}
