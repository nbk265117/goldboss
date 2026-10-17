using DofusCompanion.Application;
using DofusCompanion.Application.Knowledge;
using DofusCompanion.Application.Mechanics;
using DofusCompanion.Domain;
using DofusCompanion.Infrastructure.Knowledge;
using DofusCompanion.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DofusCompanion.Infrastructure.Tests;

public class DatabaseTests
{
    [Fact]
    public async Task Fresh_database_is_created_at_latest_schema()
    {
        using var host = new TestHost();
        var status = await host.Repository.GetStatusAsync();
        Assert.Equal(DatabaseMigrator.LatestVersion, status.SchemaVersion);
        Assert.Equal(0, status.EntityCount);
    }

    [Fact]
    public async Task Upgrade_from_v1_keeps_data_and_reaches_latest()
    {
        using var host = new TestHost(initialize: false);
        host.Repository.Initialize(targetVersion: 1);
        await host.Repository.UpsertEntityAsync(new GameEntity { Id = "boss.keep", Type = EntityType.Boss, CanonicalName = "Keep Me" }, []);

        host.Repository.Initialize(); // runs v1 -> v2
        var status = await host.Repository.GetStatusAsync();
        Assert.Equal(DatabaseMigrator.LatestVersion, status.SchemaVersion);
        Assert.NotNull(await host.Repository.GetEntityAsync("boss.keep"));
        Assert.False(await host.Repository.WasImportedAsync("x", "y")); // v2 table exists and is queryable
    }

    [Fact]
    public void Corrupt_file_is_detected()
    {
        using var host = new TestHost(initialize: false);
        File.WriteAllText(host.DatabasePath, "this is definitely not a sqlite database, it is plain text padding padding padding");
        Assert.Throws<DatabaseCorruptException>(() => host.Repository.Initialize());
    }

    [Fact]
    public void Newer_schema_than_supported_is_refused()
    {
        using var host = new TestHost();
        using (var c = new SqliteConnection($"Data Source={host.DatabasePath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 999;";
            cmd.ExecuteNonQuery();
        }

        SqliteKnowledgeRepository.ReleaseFileHandles();
        Assert.Throws<DatabaseMigrationException>(() => host.Repository.Initialize());
    }
}

public class SeedAndImportTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public async Task Bundled_seed_imports_and_is_idempotent()
    {
        using var host = new TestHost();
        var applied = await host.Import.EnsureSeedAsync();
        Assert.True(applied >= 2); // seed-core + dimensions (+ others as they ship)

        var status = await host.Repository.GetStatusAsync();
        Assert.True(status.MechanicCount >= 9);

        Assert.Equal(0, await host.Import.EnsureSeedAsync()); // unchanged seeds are not re-applied
        Assert.Equal(status.MechanicCount, (await host.Repository.GetStatusAsync()).MechanicCount);
    }

    [Fact]
    public async Task Every_seed_mechanic_cites_an_existing_source_and_is_verified_or_flagged()
    {
        using var host = new TestHost();
        await host.Import.EnsureSeedAsync();
        var sources = (await host.Repository.GetAllSourcesAsync()).Select(s => s.Id).ToHashSet();
        foreach (var m in await host.Repository.GetAllMechanicsAsync())
        {
            Assert.NotEmpty(m.Sources);
            Assert.All(m.Sources, s => Assert.Contains(s.SourceId, sources));
            Assert.All(m.Sources, s => Assert.False(string.IsNullOrWhiteSpace(s.Quote)));
            Assert.True(m.Verification is VerificationStatus.Verified or VerificationStatus.CommunityReported, m.Id);
            Assert.NotNull(m.LastVerifiedOn);
        }
    }

    [Fact]
    public async Task Seed_guidance_for_known_boss_is_complete_and_sourced()
    {
        using var host = new TestHost();
        await host.Import.EnsureSeedAsync();
        var results = await host.Knowledge.SearchAsync("kralamoure");
        var krala = Assert.Single(results, r => r.Entity.Type == EntityType.Boss);
        var g = await host.Knowledge.GetGuidanceAsync(krala.Entity.Id);
        Assert.NotNull(g);
        var mech = Assert.Single(g!.Mechanics);
        Assert.NotEmpty(mech.Sequences[0].Steps);
        Assert.Equal("https://www.dofuspourlesnoobs.com/antre-du-kralamoure-geacuteant.html", mech.Sources[0].Source.Url);
        Assert.Contains(g.Locations, l => l.Type == EntityType.Dungeon);
    }

    [Fact]
    public async Task Dungeon_guidance_includes_its_bosses_mechanics()
    {
        using var host = new TestHost();
        await host.Import.EnsureSeedAsync();
        var g = await host.Knowledge.GetGuidanceAsync("dungeon.memoire-d-orukam");
        var boss = Assert.Single(g!.ContainedBosses);
        Assert.Equal("Roi Imagami", boss.Entity.CanonicalName);
        Assert.NotEmpty(boss.Mechanics);
        Assert.False(g.NoMechanicDocumented);
    }

    [Fact]
    public async Task Every_bundled_dungeon_has_a_boss_and_most_have_mechanics()
    {
        using var host = new TestHost();
        await host.Import.EnsureSeedAsync();
        var dungeons = await host.Knowledge.GetEntitiesAsync(EntityType.Dungeon);
        Assert.Equal(131, dungeons.Count);
        var withoutMechanics = new List<string>();
        foreach (var d in dungeons)
        {
            var g = (await host.Knowledge.GetGuidanceAsync(d.Id))!;
            Assert.True(g.ContainedBosses.Count > 0, $"{d.CanonicalName} has no boss");
            if (g.NoMechanicDocumented) withoutMechanics.Add(d.CanonicalName);
        }

        // Only dungeons whose guide page documents no boss mechanic at all.
        Assert.Equal(["Akadémie des Gobs", "Ring du Capitaine Ekarlatte"], withoutMechanics.Order().ToList());
    }

    [Fact]
    public async Task Pre_dofus3_guide_is_flagged_outdated()
    {
        using var host = new TestHost();
        await host.Import.EnsureSeedAsync();
        var g = await host.Knowledge.GetGuidanceAsync("boss.tal-kasha");
        Assert.Contains(g!.Mechanics[0].Staleness, s => s.Reason == StalenessReason.SourcePredatesGameVersion);
    }

    [Fact]
    public async Task Synthetic_package_round_trips_through_database_and_export()
    {
        using var host = new TestHost();
        var plan = await host.Import.PreviewFileAsync(Fixture("synthetic.dofus-knowledge.json"));
        Assert.All(plan.Changes, c => Assert.Equal(ChangeKind.Add, c.Change));
        await host.Import.ApplyAsync(plan);

        var mech = Assert.Single(await host.Repository.GetMechanicsForEntityAsync("boss.test"));
        Assert.Equal(2, mech.Sequences.Count);
        Assert.Equal(2, mech.Sources.Count); // two quotes from the same source are both kept
        Assert.Equal(ConditionKind.TurnAtLeast, mech.Sequences[0].Steps[0].When!.Kind);
        Assert.Equal(VerificationStatus.Unknown, mech.Sequences[0].Steps[1].Verification);
        Assert.Equal("Brumeux", mech.Sequences[1].Steps[0].RequiredState);

        // Export, re-import into a fresh database: everything comes back unchanged.
        var exportPath = Path.Combine(host.Directory, "export.json");
        await host.Import.ExportToFileAsync(["boss.test"], exportPath);
        using var other = new TestHost();
        var plan2 = await other.Import.PreviewFileAsync(exportPath);
        await other.Import.ApplyAsync(plan2);
        var again = Assert.Single(await other.Repository.GetMechanicsForEntityAsync("boss.test"));
        Assert.Equal(mech.Title, again.Title);
        Assert.Equal(mech.Sequences.SelectMany(s => s.Steps).Select(s => s.Action), again.Sequences.SelectMany(s => s.Steps).Select(s => s.Action));
        Assert.NotNull(await other.Repository.GetEntityAsync("dungeon.test")); // relation target exported too

        // Re-importing the same file into the original database changes nothing.
        var plan3 = await host.Import.PreviewFileAsync(exportPath);
        Assert.DoesNotContain(plan3.Changes, c => c.Change is ChangeKind.Add or ChangeKind.Update);
    }

    [Fact]
    public async Task Invalid_package_is_rejected_and_database_untouched()
    {
        using var host = new TestHost();
        await host.Import.EnsureSeedAsync();
        var before = await host.Repository.GetStatusAsync();

        var bad = host.WriteFile("bad.json", """{ "formatVersion": 1, "packageId": "BAD ID", "name": "x", "createdOn": "2026-10-17", "sources": [], "entities": [], "mechanics": [], "evil": "<script>" }""");
        var ex = await Assert.ThrowsAsync<PackageValidationException>(() => host.Import.PreviewFileAsync(bad));
        Assert.NotEmpty(ex.Errors);

        var notJson = host.WriteFile("broken.json", "{ not json");
        await Assert.ThrowsAsync<PackageValidationException>(() => host.Import.PreviewFileAsync(notJson));

        var wrongExt = host.WriteFile("package.exe", "{}");
        await Assert.ThrowsAsync<PackageValidationException>(() => host.Import.PreviewFileAsync(wrongExt));

        var after = await host.Repository.GetStatusAsync();
        Assert.Equal(before.EntityCount, after.EntityCount);
        Assert.Equal(before.MechanicCount, after.MechanicCount);
    }

    [Fact]
    public async Task Mechanic_without_source_fails_schema()
    {
        using var host = new TestHost();
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Fixture("synthetic.dofus-knowledge.json")))!;
        doc["mechanics"]![0]!["sources"] = new System.Text.Json.Nodes.JsonArray();
        await Assert.ThrowsAsync<PackageValidationException>(() => host.Serializer.ReadAsync(doc.ToJsonString()));
    }

    [Fact]
    public async Task Duplicate_ids_inside_package_are_rejected()
    {
        using var host = new TestHost();
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Fixture("synthetic.dofus-knowledge.json")))!;
        var entities = doc["entities"]!.AsArray();
        entities.Add(entities[0]!.DeepClone());
        var ex = await Assert.ThrowsAsync<PackageValidationException>(() => host.Serializer.ReadAsync(doc.ToJsonString()));
        Assert.Contains(ex.Errors, e => e.Contains("Duplicate entity id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task User_edit_survives_seed_reapplication()
    {
        using var host = new TestHost();
        await host.Import.EnsureSeedAsync();
        var kimbo = (await host.Repository.GetEntityAsync("boss.kimbo"))!;
        await host.Repository.UpsertEntityAsync(kimbo with { Description = "Ma note perso", Origin = RecordOrigin.User }, []);

        // Simulate a new seed version by forgetting the import log.
        using (var c = new SqliteConnection($"Data Source={host.DatabasePath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM import_log";
            cmd.ExecuteNonQuery();
        }

        host.Knowledge.Invalidate();
        await host.Import.EnsureSeedAsync();
        Assert.Equal("Ma note perso", (await host.Repository.GetEntityAsync("boss.kimbo"))!.Description);
    }
}

[Collection(PerformanceCollection.Name)]
public class SearchTests
{
    private static async Task<TestHost> Seeded()
    {
        var host = new TestHost();
        var plan = await host.Import.PreviewFileAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic.dofus-knowledge.json"));
        await host.Import.ApplyAsync(plan);
        return host;
    }

    [Fact]
    public async Task Search_by_alias()
    {
        using var host = await Seeded();
        var r = await host.Knowledge.SearchAsync("synthbot");
        Assert.Equal("boss.test", r[0].Entity.Id);
        Assert.Equal(SearchMatchKind.Exact, r[0].Kind);
    }

    [Fact]
    public async Task Search_by_prefix_and_accent_insensitive()
    {
        using var host = await Seeded();
        var r = await host.Knowledge.SearchAsync("boss synth");
        Assert.Equal("boss.test", r[0].Entity.Id);
    }

    [Fact]
    public async Task Search_by_location_is_transitive()
    {
        using var host = await Seeded();
        var r = await host.Knowledge.SearchAsync("", new SearchFilter { LocationId = "area.zone-test" });
        Assert.Contains(r, x => x.Entity.Id == "boss.test"); // boss -> dungeon -> area
        Assert.Contains(r, x => x.Entity.Id == "dungeon.test");
        Assert.DoesNotContain(r, x => x.Entity.Id == "area.zone-test");
    }

    [Fact]
    public async Task Search_by_family_type_and_mechanic_type()
    {
        using var host = await Seeded();
        Assert.Equal("monster.test", Assert.Single(await host.Knowledge.SearchAsync("", new SearchFilter { FamilyId = "family.test" })).Entity.Id);
        Assert.All(await host.Knowledge.SearchAsync("", new SearchFilter { Types = new HashSet<EntityType> { EntityType.Dungeon } }), r => Assert.Equal(EntityType.Dungeon, r.Entity.Type));
        Assert.Equal("boss.test", Assert.Single(await host.Knowledge.SearchAsync("", new SearchFilter { MechanicType = MechanicType.Unlock })).Entity.Id);
    }

    [Fact]
    public async Task Search_inside_mechanic_text_finds_states()
    {
        using var host = await Seeded();
        var r = await host.Knowledge.SearchAsync("brumeux");
        Assert.Equal(SearchMatchKind.MechanicText, Assert.Single(r).Kind);
    }

    [Fact]
    public async Task Search_meets_latency_target_on_large_dataset()
    {
        using var host = new TestHost();
        // 5 000 synthetic entities ~ the size of a full monster list.
        var entities = Enumerable.Range(0, 5000).Select(i => new PackageEntity(new GameEntity
        {
            Id = $"monster.syn-{i}", Type = EntityType.Monster, CanonicalName = $"Créature Synthétique {i} {(char)('a' + i % 26)}{(char)('a' + i / 26 % 26)}",
        }, [])).ToList();
        var pkg = new KnowledgePackage { PackageId = "syn-big", Name = "big", CreatedOn = new DateOnly(2026, 10, 17), Entities = entities };
        await host.Repository.ApplyImportAsync(ImportPlanner.Plan(pkg, await host.Knowledge.GetExistingKnowledgeAsync()));
        host.Knowledge.Invalidate();

        // Several warm-up queries: JIT tiering and first-load file scanning would otherwise skew the timings.
        for (var i = 0; i < 5; i++) await host.Knowledge.SearchAsync("warm-up " + i);
        var timings = new List<double>();
        for (var i = 0; i < 21; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await host.Knowledge.SearchAsync("creature synthetiqe 42");
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        // The minimum is the intrinsic cost (target < 100 ms); the median is only a loose guard because other
        // test classes and post-build file scanning share the CPU during a full run.
        var min = timings[0];
        var median = timings[timings.Count / 2];
        Assert.True(min < 100, $"fastest search took {min:0.0} ms (median {median:0.0} ms)");
        Assert.True(median < 300, $"median search took {median:0.0} ms");
    }
}

public class SettingsTests
{
    [Fact]
    public void Settings_round_trip_and_sanitize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dc-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");

        var store = new JsonSettingsStore(path);
        store.Current.OverlayOpacity = 0.05; // would make the overlay invisible
        store.Current.Language = "en";
        store.Current.Overlay.Left = 123;
        store.Save();

        var reloaded = new JsonSettingsStore(path);
        Assert.Null(reloaded.LoadProblem);
        Assert.Equal(0.3, reloaded.Current.OverlayOpacity);
        Assert.Equal("en", reloaded.Current.Language);
        Assert.Equal(123, reloaded.Current.Overlay.Left);
        Assert.True(double.IsNaN(reloaded.Current.Overlay.Top)); // NaN survives as "not positioned yet"
        Assert.False(reloaded.Current.ScreenCaptureEnabled); // privacy defaults
        Assert.False(reloaded.Current.KeepScreenshots);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults_with_backup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dc-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ broken");

        var store = new JsonSettingsStore(path);
        Assert.NotNull(store.LoadProblem);
        Assert.Equal("fr", store.Current.Language);
        Assert.True(File.Exists(path + ".bak"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Database_path_must_be_a_db_file()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "dc-paths-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(paths.DefaultDatabase, paths.ResolveDatabase(null));
        Assert.Throws<ArgumentException>(() => paths.ResolveDatabase(@"C:\Windows\system32\config"));
        Assert.EndsWith("x.db", paths.ResolveDatabase(Path.Combine(paths.Root, "x.db")));
    }
}

/// <summary>Timing-sensitive tests run alone so parallel test classes do not skew measurements.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "Performance";
}
