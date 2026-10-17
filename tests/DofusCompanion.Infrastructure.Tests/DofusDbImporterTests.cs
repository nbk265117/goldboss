using System.Net;
using System.Text;
using System.Text.Json;
using DofusCompanion.Application.Knowledge;
using DofusCompanion.DofusDbImporter;
using DofusCompanion.Domain;

namespace DofusCompanion.Infrastructure.Tests;

/// <summary>
/// Importer tests use SYNTHETIC records shaped like DofusDB API responses. No test touches the network.
/// </summary>
public class DofusDbImporterTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static DofusDbSnapshot Snapshot() => new(
        Dungeons: [J("""{"id":9001,"name":{"fr":"Donjon Synthétique","en":"Synthetic Dungeon"},"minLevel":20,"optimalPlayerLevel":30,"subarea":501,"monsters":[7001],"bosses":[7002]}""")],
        Monsters:
        [
            J("""{"id":7001,"name":{"fr":"Mob Synthétique","en":"Synthetic Mob"},"isBoss":false,"race":31,"grades":[{"level":18},{"level":22}],"subareas":[501]}"""),
            J("""{"id":7002,"name":{"fr":"Kimbo","en":"Kimbo"},"isBoss":true,"race":31,"grades":[{"level":150}]}"""),
            J("""{"id":7003,"name":{"fr":"Errant Synthétique"},"isBoss":false,"subareas":[501, 999]}"""),
            J("""{"id":7004}"""), // no name: skipped with a warning
        ],
        Races: [J("""{"id":31,"name":{"fr":"Famille Synthétique"}}""")],
        SubAreas: [J("""{"id":501,"name":{"fr":"Sous-zone Synthétique"},"areaId":42}""")],
        Areas: [J("""{"id":42,"name":{"fr":"Dimension Synthétique","en":"Synthetic Dimension"}}""")]);

    [Fact]
    public void Maps_entities_relations_and_provenance()
    {
        var r = DofusDbMapper.Map(Snapshot(), new DateOnly(2026, 10, 17));
        var byId = r.Package.Entities.ToDictionary(e => e.Entity.Id);

        Assert.Equal(EntityType.Boss, byId["boss.dofusdb-7002"].Entity.Type);
        Assert.Equal(EntityType.Monster, byId["monster.dofusdb-7001"].Entity.Type);
        Assert.Equal("18-22", byId["monster.dofusdb-7001"].Entity.LevelText);
        Assert.Contains(byId["monster.dofusdb-7001"].Entity.Aliases, a => a.Text == "Synthetic Mob" && a.Locale == "en");
        Assert.Contains(byId["monster.dofusdb-7001"].Relations, x => x.Type == RelationType.LocatedIn && x.ToId == "dungeon.dofusdb-9001");
        Assert.Contains(byId["monster.dofusdb-7001"].Relations, x => x.Type == RelationType.MemberOfFamily && x.ToId == "family.dofusdb-31");
        Assert.Contains(byId["dungeon.dofusdb-9001"].Relations, x => x.ToId == "area.dofusdb-sub-501");
        Assert.Contains(byId["area.dofusdb-sub-501"].Relations, x => x.ToId == "area.dofusdb-42");
        // Unknown sub-area 999 is dropped, never invented.
        Assert.DoesNotContain(byId["monster.dofusdb-7003"].Relations, x => x.ToId.EndsWith("999", StringComparison.Ordinal));
        Assert.Single(r.Warnings, w => w.Contains("monster", StringComparison.Ordinal));

        Assert.All(r.Package.Entities, e => Assert.Equal(VerificationStatus.CommunityReported, e.Entity.Verification));
        Assert.Empty(r.Package.Mechanics); // DofusDB gives data, not strategies
        Assert.Equal(SourceReliability.Community, Assert.Single(r.Package.Sources).Reliability);
    }

    [Fact]
    public async Task Output_passes_the_application_schema_and_merges_with_existing_bosses()
    {
        using var host = new TestHost();
        await host.Import.EnsureSeedAsync();
        var package = DofusDbMapper.Map(Snapshot(), new DateOnly(2026, 10, 17)).Package;
        var json = host.Serializer.Write(package);
        var reread = await host.Serializer.ReadAsync(json); // schema + semantic validation

        var plan = ImportPlanner.Plan(reread, await host.Knowledge.GetExistingKnowledgeAsync());
        // "Kimbo" from DofusDB is the same boss as the verified seed entry: merged, not duplicated.
        var kimbo = plan.Changes.Single(c => c.Kind == RecordKind.Entity && c.DisplayName == "Kimbo");
        Assert.Equal(ConflictReason.PossibleDuplicate, kimbo.Conflict);
        Assert.Equal(Resolution.MergeIntoExisting, kimbo.Resolution);
        await host.Import.ApplyAsync(plan);

        var merged = (await host.Repository.GetEntityAsync("boss.kimbo"))!;
        Assert.Equal(VerificationStatus.Verified, merged.Verification); // seed provenance preserved
        Assert.Equal("150", merged.LevelText); // missing level filled from DofusDB
        Assert.Null(await host.Repository.GetEntityAsync("boss.dofusdb-7002"));
        Assert.NotEmpty(await host.Repository.GetMechanicsForEntityAsync("boss.kimbo"));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Page(int total, int skip, int count) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            total, limit = 2, skip,
            data = Enumerable.Range(skip, count).Select(i => new { id = i, name = new { fr = $"N{i}" } }),
        }), Encoding.UTF8, "application/json"),
    };

    private static string TempCache() => Path.Combine(Path.GetTempPath(), "dc-dofusdb-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Client_paginates_caches_and_does_not_refetch()
    {
        var cache = TempCache();
        var handler = new FakeHandler(req =>
        {
            var skip = int.Parse(System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query)["$skip"]!, System.Globalization.CultureInfo.InvariantCulture);
            return Page(total: 5, skip, Math.Min(2, 5 - skip));
        });
        var options = new ClientOptions { BaseUri = new Uri("https://example.invalid/"), CacheDirectory = cache, Delay = TimeSpan.Zero, PageSize = 2 };

        var first = new DofusDbClient(new HttpClient(handler), options, TextWriter.Null);
        Assert.Equal(5, (await first.GetAllAsync("monsters", CancellationToken.None)).Count);
        Assert.Equal(3, first.NetworkRequests);

        var second = new DofusDbClient(new HttpClient(handler), options, TextWriter.Null);
        Assert.Equal(5, (await second.GetAllAsync("monsters", CancellationToken.None)).Count);
        Assert.Equal(0, second.NetworkRequests); // served from cache
        Directory.Delete(cache, true);
    }

    [Fact]
    public async Task Client_stops_on_forbidden_and_respects_request_cap_and_offline()
    {
        var forbidden = new DofusDbClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))),
            new ClientOptions { BaseUri = new Uri("https://example.invalid/"), CacheDirectory = TempCache(), Delay = TimeSpan.Zero }, TextWriter.Null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => forbidden.GetAllAsync("monsters", CancellationToken.None));
        Assert.Contains("403", ex.Message);

        var capped = new DofusDbClient(new HttpClient(new FakeHandler(req => Page(100, 0, 2))),
            new ClientOptions { BaseUri = new Uri("https://example.invalid/"), CacheDirectory = TempCache(), Delay = TimeSpan.Zero, MaxRequests = 1, PageSize = 2 }, TextWriter.Null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capped.GetAllAsync("monsters", CancellationToken.None));
        Assert.Equal(1, capped.NetworkRequests);

        var offline = new DofusDbClient(new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("network used"))),
            new ClientOptions { CacheDirectory = TempCache(), Offline = true }, TextWriter.Null);
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => offline.GetAllAsync("monsters", CancellationToken.None));
        Assert.Contains("Offline", off.Message);
    }
}
