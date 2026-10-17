using DofusCompanion.Application.Knowledge;
using DofusCompanion.Domain;

namespace DofusCompanion.Core.Tests;

public class ImportPlannerTests
{
    private static ExistingKnowledge Existing(IEnumerable<GameEntity>? entities = null, IEnumerable<Mechanic>? mechanics = null,
        IEnumerable<KnowledgeSource>? sources = null, IEnumerable<EntityRelation>? relations = null) =>
        new([], (sources ?? [TestData.Source()]).ToList(), (entities ?? []).ToList(), (relations ?? []).ToList(), (mechanics ?? []).ToList());

    private static KnowledgePackage Package(IEnumerable<PackageEntity>? entities = null, IEnumerable<Mechanic>? mechanics = null,
        IEnumerable<KnowledgeSource>? sources = null) => new()
    {
        PackageId = "pkg.test",
        Name = "test",
        CreatedOn = new DateOnly(2026, 10, 17),
        Sources = (sources ?? []).ToList(),
        Entities = (entities ?? []).ToList(),
        Mechanics = (mechanics ?? []).ToList(),
    };

    private static PackageEntity PE(GameEntity e, params EntityRelation[] r) => new(e, r);

    [Fact]
    public void New_records_are_added()
    {
        var plan = ImportPlanner.Plan(Package([PE(TestData.Entity("boss.test", "Test"))], [TestData.Mechanic()]), Existing());
        Assert.All(plan.Changes, c => Assert.Equal(ChangeKind.Add, c.Change));
        Assert.Equal(2, plan.Changes.Count);
    }

    [Fact]
    public void Identical_records_are_unchanged()
    {
        var e = TestData.Entity("boss.test", "Test");
        var plan = ImportPlanner.Plan(Package([PE(e with { Origin = RecordOrigin.Import })]), Existing([e with { Origin = RecordOrigin.Seed }]));
        Assert.Equal(ChangeKind.Unchanged, Assert.Single(plan.Changes).Change);
    }

    [Fact]
    public void Verified_record_is_never_silently_overwritten()
    {
        var existing = TestData.Entity("boss.test", "Test") with { Description = "verified text" };
        var incoming = existing with { Description = "different text" };
        var change = Assert.Single(ImportPlanner.Plan(Package([PE(incoming)]), Existing([existing])).Changes);

        Assert.Equal(ChangeKind.Conflict, change.Change);
        Assert.Equal(ConflictReason.ExistingVerified, change.Conflict);
        Assert.Equal(Resolution.KeepExisting, change.Resolution);
        Assert.Null(change.ToWrite); // default resolution writes nothing
    }

    [Fact]
    public void User_edited_record_conflicts_even_if_unverified()
    {
        var existing = TestData.Entity("boss.test", "Test") with { Verification = VerificationStatus.Unknown, Origin = RecordOrigin.User, Description = "mine" };
        var change = Assert.Single(ImportPlanner.Plan(Package([PE(existing with { Description = "theirs", Origin = RecordOrigin.Import })]), Existing([existing])).Changes);
        Assert.Equal(ConflictReason.ExistingUserEdited, change.Conflict);
    }

    [Fact]
    public void Unverified_imported_record_is_updated_by_equal_or_better_provenance()
    {
        var existing = TestData.Entity("boss.test", "Test") with { Verification = VerificationStatus.CommunityReported, Origin = RecordOrigin.Import };
        var change = Assert.Single(ImportPlanner.Plan(Package([PE(existing with { Description = "better", Verification = VerificationStatus.Verified })]), Existing([existing])).Changes);
        Assert.Equal(ChangeKind.Update, change.Change);
        Assert.NotNull(change.ToWrite);
    }

    [Fact]
    public void Weaker_provenance_conflicts()
    {
        var existing = TestData.Mechanic(v: VerificationStatus.CommunityReported) with { Origin = RecordOrigin.Import };
        var incoming = existing with { Verification = VerificationStatus.Inferred, Summary = "guess" };
        var plan = ImportPlanner.Plan(Package(mechanics: [incoming]), Existing([TestData.Entity("boss.test", "T")], [existing]));
        Assert.Equal(ConflictReason.WeakerProvenance, Assert.Single(plan.Changes).Conflict);
    }

    [Fact]
    public void Same_name_different_id_is_flagged_duplicate_and_merged_by_default()
    {
        var existing = TestData.Entity("boss.alpha", "Gardien Alpha", EntityType.Boss, "Alpha");
        var incoming = TestData.Entity("boss.other-id", "Gardien  ALPHA", EntityType.Boss, "Guardian Alpha") with
        {
            Attributes = new Dictionary<string, string> { ["level"] = "200" },
        };
        var plan = ImportPlanner.Plan(Package([PE(incoming)], [TestData.Mechanic("m1", "boss.other-id")]), Existing([existing]));

        var dup = plan.Changes.Single(c => c.Kind == RecordKind.Entity);
        Assert.Equal(ConflictReason.PossibleDuplicate, dup.Conflict);
        Assert.Equal(Resolution.MergeIntoExisting, dup.Resolution);
        Assert.DoesNotContain(Resolution.TakeIncoming, dup.AllowedResolutions);

        var merged = Assert.IsType<PackageEntity>(dup.ToWrite);
        Assert.Equal("boss.alpha", merged.Entity.Id);
        Assert.Contains(merged.Entity.Aliases, a => a.Text == "Guardian Alpha");
        Assert.Contains(merged.Entity.Aliases, a => a.Text == "Alpha");
        Assert.Equal("200", merged.Entity.Attributes["level"]);

        // The mechanic is re-pointed to the existing entity id.
        var mech = Assert.IsType<Mechanic>(plan.Changes.Single(c => c.Kind == RecordKind.Mechanic).ToWrite);
        Assert.Equal("boss.alpha", mech.EntityId);
    }

    [Fact]
    public void Merge_never_overwrites_existing_attribute()
    {
        var existing = TestData.Entity("boss.a", "A Boss") with { Attributes = new Dictionary<string, string> { ["level"] = "100" } };
        var incoming = TestData.Entity("boss.a", "A Boss") with { Attributes = new Dictionary<string, string> { ["level"] = "999" }, Description = "x" };
        var change = Assert.Single(ImportPlanner.Plan(Package([PE(incoming)]), Existing([existing])).Changes);
        Assert.Equal("100", change.Merged!.Entity.Attributes["level"]);
    }

    [Fact]
    public void Mechanic_for_unknown_entity_is_skipped()
    {
        var change = Assert.Single(ImportPlanner.Plan(Package(mechanics: [TestData.Mechanic(entityId: "boss.ghost")]), Existing()).Changes);
        Assert.Equal(ChangeKind.Skip, change.Change);
        Assert.Contains("boss.ghost", change.Reason);
    }

    [Fact]
    public void Mechanic_citing_unknown_source_is_skipped()
    {
        var m = TestData.Mechanic() with { Sources = [new SourceReference("src.nowhere")] };
        var plan = ImportPlanner.Plan(Package([PE(TestData.Entity("boss.test", "T"))], [m]), Existing());
        Assert.Equal(ChangeKind.Skip, plan.Changes.Single(c => c.Kind == RecordKind.Mechanic).Change);
    }

    [Fact]
    public void Relation_to_unknown_entity_is_dropped_with_warning()
    {
        var plan = ImportPlanner.Plan(Package([PE(TestData.Entity("boss.test", "T"), new EntityRelation("boss.test", RelationType.LocatedIn, "dungeon.nope"))]), Existing());
        Assert.Empty(Assert.IsType<PackageEntity>(plan.Changes[0].ToWrite).Relations);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void Test_only_packages_are_refused() =>
        Assert.Throws<InvalidOperationException>(() => ImportPlanner.Plan(Package() with { TestOnly = true }, Existing()));

    [Fact]
    public void Changed_source_conflicts_instead_of_overwriting()
    {
        var plan = ImportPlanner.Plan(Package(sources: [TestData.Source() with { Title = "renamed" }]), Existing());
        Assert.Equal(ChangeKind.Conflict, Assert.Single(plan.Changes).Change);
    }

    [Fact]
    public void Seed_origin_is_applied_to_incoming_records()
    {
        var plan = ImportPlanner.Plan(Package([PE(TestData.Entity("boss.test", "T"))]), Existing(), RecordOrigin.Seed);
        Assert.Equal(RecordOrigin.Seed, Assert.IsType<PackageEntity>(plan.Changes[0].Incoming).Entity.Origin);
    }

    [Fact]
    public void Import_never_removes_existing_relations()
    {
        var boss = TestData.Entity("boss.test", "T");
        var dungeon = TestData.Entity("dj.x", "Dj", EntityType.Dungeon);
        var existingRel = new EntityRelation("boss.test", RelationType.LocatedIn, "dj.x");
        var plan = ImportPlanner.Plan(Package([PE(boss)]), Existing([boss, dungeon], relations: [existingRel]));
        var change = Assert.Single(plan.Changes);
        Assert.Equal(ChangeKind.Unchanged, change.Change); // a package without the relation is not a change
    }
}
