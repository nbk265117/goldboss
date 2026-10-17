using DofusCompanion.Application.Imaging;
using DofusCompanion.Application.Recognition;
using DofusCompanion.Domain;

namespace DofusCompanion.Core.Tests;

public class AutoDetectionTests
{
    private static RecognizedCandidate C(string id, EntityType type, double score) =>
        new(new GameEntity { Id = id, Type = type, CanonicalName = id }, score, id, id, null, "original");

    [Fact]
    public void Exact_boss_is_shown_immediately()
    {
        var d = new AutoDetectDecider();
        var r = d.Decide([C("boss.a", EntityType.Boss, 1.0)]);
        Assert.Equal(AutoDetectAction.Show, r.Action);
        Assert.Equal("boss.a", d.CurrentId);
    }

    [Fact]
    public void Fuzzy_or_non_boss_needs_two_consistent_scans()
    {
        var d = new AutoDetectDecider();
        Assert.Equal(AutoDetectAction.Pending, d.Decide([C("dungeon.x", EntityType.Dungeon, 1.0)]).Action);
        Assert.Equal(AutoDetectAction.Show, d.Decide([C("dungeon.x", EntityType.Dungeon, 1.0)]).Action);
        Assert.Equal(AutoDetectAction.None, d.Decide([C("dungeon.x", EntityType.Dungeon, 1.0)]).Action); // already shown
    }

    [Fact]
    public void Glimpse_in_chat_does_not_switch()
    {
        var d = new AutoDetectDecider();
        d.Decide([C("dungeon.x", EntityType.Dungeon, 1.0)]);
        d.Decide([C("dungeon.x", EntityType.Dungeon, 1.0)]);
        Assert.Equal(AutoDetectAction.Pending, d.Decide([C("monster.chat", EntityType.Monster, 0.95)]).Action);
        Assert.Equal(AutoDetectAction.None, d.Decide([C("dungeon.x", EntityType.Dungeon, 1.0)]).Action);
        Assert.Equal("dungeon.x", d.CurrentId);
    }

    [Fact]
    public void Boss_has_priority_over_area_and_monsters_are_collected()
    {
        var d = new AutoDetectDecider();
        var r = d.Decide([C("area.z", EntityType.Area, 1.0), C("monster.m", EntityType.Monster, 0.97), C("boss.b", EntityType.Boss, 1.0)]);
        Assert.Equal("boss.b", r.Entity!.Id);
        Assert.Equal(2, r.MonstersSeen.Count); // the monster and the boss are both in the fight
    }

    [Fact]
    public void One_shot_pick_shows_best_by_priority_without_confirmation()
    {
        var r = AutoDetectDecider.Pick([C("area.z", EntityType.Area, 1.0), C("dungeon.x", EntityType.Dungeon, 0.93), C("monster.m", EntityType.Monster, 0.5)]);
        Assert.Equal(AutoDetectAction.Show, r.Action);
        Assert.Equal("dungeon.x", r.Entity!.Id);
        Assert.Empty(r.MonstersSeen); // the low-score monster is not trusted
        Assert.Equal(AutoDetectAction.None, AutoDetectDecider.Pick([C("boss.a", EntityType.Boss, 0.7)]).Action);
    }

    [Fact]
    public void Low_scores_are_ignored()
    {
        var d = new AutoDetectDecider();
        Assert.Equal(AutoDetectAction.None, d.Decide([C("boss.a", EntityType.Boss, 0.8)]).Action);
    }

    [Fact]
    public void Fingerprint_detects_change_but_not_identity()
    {
        var a = RasterImage.Blank(320, 180, 20);
        var b = RasterImage.Blank(320, 180, 20);
        var c = RasterImage.Blank(320, 180, 200);
        Assert.Equal(0, ImageFingerprint.Of(a).DistanceTo(ImageFingerprint.Of(b)));
        Assert.True(ImageFingerprint.Of(a).DistanceTo(ImageFingerprint.Of(c)) > 100);
    }
}
