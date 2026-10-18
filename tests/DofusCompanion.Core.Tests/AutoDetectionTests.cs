using DofusCompanion.Application.Imaging;
using DofusCompanion.Application.Recognition;
using DofusCompanion.Domain;

namespace DofusCompanion.Core.Tests;

public class AutoDetectionTests
{
    private static RecognizedCandidate C(string id, EntityType type, double score, string? line = null) =>
        new(new GameEntity { Id = id, Type = type, CanonicalName = id }, score, id, line ?? id, null, "original");

    [Fact]
    public void Real_case_chat_message_about_kralamoure_while_in_royalmouth()
    {
        const string chat = "[16:32] Les portes de l'Antre du Kralamoure Géant sont";
        var candidates = new[]
        {
            C("dungeon.antre-du-kralamoure-geant", EntityType.Dungeon, 1.0, chat),
            C("boss.kralamoure-geant", EntityType.Boss, 1.0, chat),
            C("boss.royalmouth", EntityType.Boss, 1.0, "Serre du Royalmouth"),
            C("dungeon.serre-du-royalmouth", EntityType.Dungeon, 1.0, "Serre du Royalmouth"),
        };
        var bossesIn = new Dictionary<string, IReadOnlySet<string>>
        {
            ["dungeon.antre-du-kralamoure-geant"] = new HashSet<string> { "boss.kralamoure-geant" },
            ["dungeon.serre-du-royalmouth"] = new HashSet<string> { "boss.royalmouth" },
        };
        Assert.Equal("boss.royalmouth", AutoDetectDecider.Pick(candidates, bossesIn: bossesIn).Entity!.Id);
        Assert.True(AutoDetectDecider.IsChatLine("[16:05] (Guilde) Message de guilde"));
        Assert.False(AutoDetectDecider.IsChatLine("Serre du Royalmouth"));
    }

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
    public void Boss_read_in_chat_does_not_win_over_the_dungeon_on_screen()
    {
        // In Royalmouth's room the map name is read, and "Kralamoure" appears in the chat.
        var bossesIn = new Dictionary<string, IReadOnlySet<string>> { ["dungeon.serre"] = new HashSet<string> { "boss.royalmouth" } };
        var chatOnly = AutoDetectDecider.Pick([C("boss.kralamoure", EntityType.Boss, 1.0), C("dungeon.serre", EntityType.Dungeon, 1.0)], bossesIn: bossesIn);
        Assert.Equal("dungeon.serre", chatOnly.Entity!.Id); // the dungeon sheet shows Royalmouth's steps

        var both = AutoDetectDecider.Pick([C("boss.kralamoure", EntityType.Boss, 1.0), C("boss.royalmouth", EntityType.Boss, 0.95), C("dungeon.serre", EntityType.Dungeon, 1.0)], bossesIn: bossesIn);
        Assert.Equal("boss.royalmouth", both.Entity!.Id);

        // No place recognised: unchanged behaviour.
        Assert.Equal("boss.kralamoure", AutoDetectDecider.Pick([C("boss.kralamoure", EntityType.Boss, 1.0)]).Entity!.Id);
    }

    [Theory]
    [InlineData("Serre du Royalmouth -82,-48", -82, -48)]
    [InlineData("[-82, -48] Niveau 190", -82, -48)]
    [InlineData("Ile de Frigost -82 , -48", -82, -48)]
    [InlineData("pos 3,7", 3, 7)]
    public void Map_coordinates_are_read(string text, int x, int y) => Assert.Contains((x, y), MapCoordinates.Find(text));

    [Theory]
    [InlineData("1,5 % de chance")]
    [InlineData("12,345 kamas")]
    [InlineData("3,14.5")]
    public void Numbers_that_are_not_coordinates_are_ignored(string text) => Assert.Empty(MapCoordinates.Find(text));

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

    [Fact]
    public void Real_case_songes_short_word_is_not_a_monster()
    {
        // Songes, 2026-10-17: "de" in "Songe de : Faouzi-ahmar" matched the monster "Dé" and hid the zone.
        static RecognizedCandidate N(string id, EntityType type, string name, string line) =>
            new(new GameEntity { Id = id, Type = type, CanonicalName = name }, 1.0, name, line, null, "original");
        var pick = AutoDetectDecider.Pick(
        [
            N("monster.de", EntityType.Monster, "Dé", "Songe de : Faouzi-ahmar"),
            N("area.songes", EntityType.Area, "Songes Infinis", "@ SONGES INFINIS"),
        ]);
        Assert.Equal("area.songes", pick.Entity!.Id);
        Assert.Empty(pick.MonstersSeen);

        // Alone on its line (a hovered monster's tooltip), the short name still counts.
        Assert.Equal("monster.de", AutoDetectDecider.Pick([N("monster.de", EntityType.Monster, "Dé", "Dé")]).Entity!.Id);
    }
}

public class VariantBossTests
{
    [Fact]
    public void Boss_variant_without_mechanics_uses_its_base_boss()
    {
        GameEntity E(string id, string name, EntityType t = EntityType.Boss) => new() { Id = id, Type = t, CanonicalName = name };
        var entities = new[]
        {
            E("boss.tynril", "Tynril"), E("boss.tynril-ahuri", "Tynril Ahuri"), E("boss.grozilla-fatigue", "Grozilla Fatigué"),
            E("boss.ombre", "Ombre"), E("monster.ombre-ailee", "Ombre Ailée", EntityType.Monster), E("boss.tynrils", "Tynrilsss"),
        };
        var mechanics = new[]
        {
            new Mechanic { Id = "m1", EntityId = "boss.tynril", Title = "t" },
            new Mechanic { Id = "m2", EntityId = "boss.ombre", Title = "t" },
        };
        var v = DofusCompanion.Application.KnowledgeService.VariantBases(entities, mechanics);
        Assert.Equal("boss.tynril", v["boss.tynril-ahuri"]);
        Assert.False(v.ContainsKey("monster.ombre-ailee")); // a different monster, not a variant of the boss
        Assert.False(v.ContainsKey("boss.tynrils"));       // whole word only
        Assert.False(v.ContainsKey("boss.grozilla-fatigue")); // its base has no mechanics here
    }
}

public class ImageFallbackTests
{
    [Fact]
    public void Entities_without_picture_borrow_the_same_thing_s_picture()
    {
        GameEntity E(string id, EntityType t, string name, string? img = null) => new()
        {
            Id = id, Type = t, CanonicalName = name,
            Attributes = img is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["image"] = img },
        };
        var entities = new[]
        {
            E("boss.guerre-db", EntityType.Boss, "Guerre", "dofusdb/1.png"),
            E("boss.guerre-tempete", EntityType.Boss, "Guerre (Tempête de l'Eliocalypse)"),
            E("boss.tynril-ahuri", EntityType.Boss, "Tynril Ahuri", "dofusdb/2.png"),
            E("boss.tynril", EntityType.Boss, "Tynril"),
            E("dungeon.labo", EntityType.Dungeon, "Laboratoire du Tynril"),
            E("dungeon.expe", EntityType.Dungeon, "Expédition - Laboratoire du Tynril"),
            E("dungeon.empty", EntityType.Dungeon, "Donjon vide"),
            E("boss.db-chouque", EntityType.Boss, "Le Chouque", "dofusdb/3.png"),
            E("area.labo", EntityType.Area, "Laboratoire du Tynril"),
            E("boss.chouque", EntityType.Boss, "Chouque") with { Aliases = [new EntityAlias("Le Chouque", "fr", RecordOrigin.Seed)] },
        };
        var relations = new[] { new EntityRelation("boss.tynril", RelationType.LocatedIn, "dungeon.labo") };
        var img = DofusCompanion.Application.KnowledgeService.WithImageFallbacks(entities, relations)
            .ToDictionary(e => e.Id, e => e.Attributes.GetValueOrDefault("image"));
        Assert.Equal("dofusdb/1.png", img["boss.guerre-tempete"]);
        Assert.Equal("dofusdb/2.png", img["boss.tynril"]);
        Assert.Equal("dofusdb/2.png", img["dungeon.labo"]);   // from its boss
        Assert.Equal("dofusdb/2.png", img["dungeon.expe"]);   // from the original dungeon
        Assert.Null(img["dungeon.empty"]);
        Assert.Equal("dofusdb/3.png", img["boss.chouque"]); // through its alias
        Assert.Equal("dofusdb/2.png", img["area.labo"]);    // the dungeon of the same name
    }
}
