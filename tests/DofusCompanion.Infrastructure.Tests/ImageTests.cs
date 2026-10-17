using System.Net;
using System.Text.Json;
using DofusCompanion.Application.Knowledge;
using DofusCompanion.Domain;
using DofusCompanion.DofusDbImporter;

namespace DofusCompanion.Infrastructure.Tests;

public sealed class ImageTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-images-" + Guid.NewGuid().ToString("N"));
    private readonly EntityImageStore _store;

    public ImageTests() => _store = new EntityImageStore(new AppPaths(_root));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static GameEntity Entity(string id, string? image = null) => new()
    {
        Id = id,
        Type = EntityType.Boss,
        CanonicalName = "Test",
        Attributes = image is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["image"] = image },
    };

    [Fact]
    public void User_picture_is_saved_resolved_and_removed()
    {
        Assert.Null(_store.Resolve(Entity("boss.test")));
        var path = _store.SaveUserImage("boss.test", Png);
        Assert.Equal(path, _store.Resolve(Entity("boss.test")));
        Assert.True(_store.HasUserImage("boss.test"));
        Assert.True(_store.RemoveUserImage("boss.test"));
        Assert.Null(_store.Resolve(Entity("boss.test")));
    }

    [Fact]
    public void Attribute_picture_resolves_inside_images_folder_only()
    {
        Directory.CreateDirectory(Path.Combine(_store.Directory, "dofusdb"));
        File.WriteAllBytes(Path.Combine(_store.Directory, "dofusdb", "31.png"), Png);
        File.WriteAllBytes(Path.Combine(_root, "secret.png"), Png);

        Assert.NotNull(_store.Resolve(Entity("boss.a", "dofusdb/31.png")));
        Assert.Null(_store.Resolve(Entity("boss.b", "../secret.png")));                 // traversal
        Assert.Null(_store.Resolve(Entity("boss.c", Path.Combine(_root, "secret.png")))); // absolute path
        Assert.Null(_store.Resolve(Entity("boss.d", "dofusdb/31.exe")));                 // not an image extension
        Assert.Null(_store.Resolve(Entity("boss.e", "dofusdb/missing.png")));
    }

    [Fact]
    public void User_picture_takes_precedence_over_attribute()
    {
        Directory.CreateDirectory(Path.Combine(_store.Directory, "dofusdb"));
        File.WriteAllBytes(Path.Combine(_store.Directory, "dofusdb", "31.png"), Png);
        var own = _store.SaveUserImage("boss.a", Png);
        Assert.Equal(own, _store.Resolve(Entity("boss.a", "dofusdb/31.png")));
    }

    [Fact]
    public void Oversized_picture_is_refused() =>
        Assert.Throws<IOException>(() => _store.SaveUserImage("boss.big", new byte[EntityImageStore.MaxFileBytes + 1]));

    [Fact]
    public void Image_url_uses_record_field_or_template_and_only_dofusdb_https()
    {
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();
        Assert.Equal("https://api.dofusdb.fr/img/monsters/31.png",
            DofusDbMapper.ImageUrl(J("""{"id":31,"img":"https://api.dofusdb.fr/img/monsters/31.png"}"""), null)!.ToString());
        Assert.Equal("https://api.dofusdb.fr/img/monsters/7.png",
            DofusDbMapper.ImageUrl(J("""{"id":7}"""), "https://api.dofusdb.fr/img/monsters/{id}.png")!.ToString());
        Assert.Null(DofusDbMapper.ImageUrl(J("""{"id":7,"img":"http://api.dofusdb.fr/x.png"}"""), null));   // not https
        Assert.Null(DofusDbMapper.ImageUrl(J("""{"id":7,"img":"https://evil.example/x.png"}"""), null));    // other host
        Assert.Null(DofusDbMapper.ImageUrl(J("""{"name":"no id"}"""), "https://api.dofusdb.fr/img/monsters/{id}.png"));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(f(r));
        }
    }

    [Fact]
    public async Task Download_keeps_real_images_only_and_is_cached()
    {
        var handler = new Handler(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(r.RequestUri!.AbsolutePath.EndsWith("good.png", StringComparison.Ordinal) ? Png : "<html>not an image</html>"u8.ToArray()),
        });
        var client = new DofusDbClient(new HttpClient(handler), new ClientOptions { CacheDirectory = Path.Combine(_root, "cache"), Delay = TimeSpan.Zero }, TextWriter.Null);
        var good = Path.Combine(_root, "img", "good.png");
        var bad = Path.Combine(_root, "img", "bad.png");

        Assert.True(await client.DownloadImageAsync(new Uri("https://api.dofusdb.fr/good.png"), good, CancellationToken.None));
        Assert.False(await client.DownloadImageAsync(new Uri("https://api.dofusdb.fr/bad.png"), bad, CancellationToken.None));
        Assert.False(File.Exists(bad));
        Assert.True(await client.DownloadImageAsync(new Uri("https://api.dofusdb.fr/good.png"), good, CancellationToken.None));
        Assert.Equal(2, handler.Calls); // the second request for good.png was served from disk
    }

    [Fact]
    public async Task DofusDb_monster_picture_reaches_the_existing_boss_through_merge()
    {
        using var host = new TestHost();
        await host.Import.EnsureSeedAsync();
        // DofusDB lists wanted-notice targets and many bosses as plain monsters (isBoss = false).
        var snapshot = new DofusDbSnapshot([], [JsonDocument.Parse("""{"id":555,"name":{"fr":"Crasper"},"isBoss":false}""").RootElement.Clone()], [], [], []);
        var package = DofusDbMapper.Map(snapshot, new DateOnly(2026, 10, 17), imageFor: id => DofusDbMapper.ImageRelativePath(id)).Package;

        var plan = ImportPlanner.Plan(package, await host.Knowledge.GetExistingKnowledgeAsync());
        var change = Assert.Single(plan.Changes, c => c.Kind == RecordKind.Entity);
        Assert.Equal(ConflictReason.PossibleDuplicate, change.Conflict); // Monster "Crasper" == Boss "Crasper"
        await host.Import.ApplyAsync(plan);

        var boss = (await host.Repository.GetEntityAsync(change.Id))!;
        Assert.Equal(EntityType.Boss, boss.Type);
        Assert.Equal("dofusdb/555.png", boss.Attributes["image"]);
    }

    [Fact]
    public async Task Dropped_packages_are_applied_at_startup_and_archived()
    {
        using var host = new TestHost();
        var drop = Path.Combine(host.Directory, "imports");
        Directory.CreateDirectory(drop);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic.dofus-knowledge.json"), Path.Combine(drop, "good.json"));
        File.WriteAllText(Path.Combine(drop, "bad.json"), "{ not json");

        var results = await host.Import.ApplyDroppedPackagesAsync(drop);

        Assert.Equal(2, results.Count);
        Assert.NotNull(await host.Repository.GetEntityAsync("boss.test"));        // valid package applied
        Assert.Empty(Directory.GetFiles(drop, "*.json"));                         // nothing left to re-apply
        Assert.Single(Directory.GetFiles(Path.Combine(drop, "applied")));
        Assert.Single(Directory.GetFiles(Path.Combine(drop, "rejected")));
    }

    [Fact]
    public void Importer_knows_bundled_boss_names_for_picture_filtering()
    {
        var names = KnownNames.Bosses();
        Assert.Contains("royalmouth", names);
        Assert.Contains("kimbo", names);
        Assert.Contains("crasper", names);   // wanted-notice target
        Assert.Contains("guerre", names);    // "Guerre (Tempête de l'Eliocalypse)" -> "guerre"
    }
}
