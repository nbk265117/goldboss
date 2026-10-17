using System.Globalization;
using DofusCompanion.DofusDbImporter;
using DofusCompanion.Infrastructure.Knowledge;

// dofusdb-import — builds a Dofus Gold Boss - DGB knowledge package (monsters, bosses, dungeons, areas,
// families) from the DofusDB public API. Run it YOURSELF; the application itself never goes online.

const string Usage = """
Usage: dofusdb-import --accept-terms [options]

  --accept-terms        Required for network access. Confirms you checked DofusDB's conditions of use
                        (and Ankama's CGU) and accept responsibility for this personal, low-volume use.
  --out <file.json>     Output package (default: the app's imports folder, applied automatically at next start)
  --cache <dir>         Cache folder for raw API pages (default: dofusdb-cache)
  --delay-ms <n>        Pause between requests, minimum 1000 (default: 1500)
  --max-requests <n>    Network request cap for this run (default: 600)
  --offline             Use only the cache, never the network
  --refresh             Re-download pages even if cached
  --lang <fr|en|...>    Language of canonical names (default: fr)
  --images              Also download pictures (personal use) of every boss, into the app's images folder
  --all-images          With --images: download the pictures of every monster too (~5,000; about 2 hours)
  --images-dir <dir>    Pictures folder (default: %LOCALAPPDATA%\DofusGoldBoss\images)
  --image-url <tpl>     Picture URL template when a record has none (default: https://api.dofusdb.fr/img/monsters/{id}.png)

Then import the file in Dofus Gold Boss - DGB: Connaissances › Importer un paquet… (preview before applying).
""";

var a = args.ToList();
string? Opt(string name) { var i = a.IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }
bool Flag(string name) => a.Contains(name);

if (Flag("--help") || Flag("-h")) { Console.WriteLine(Usage); return 0; }

var offline = Flag("--offline");
if (!offline && !Flag("--accept-terms"))
{
    Console.Error.WriteLine("Refusing to access the network without --accept-terms.\n");
    Console.Error.WriteLine(Usage);
    return 2;
}

var options = new ClientOptions
{
    CacheDirectory = Opt("--cache") ?? "dofusdb-cache",
    Delay = TimeSpan.FromMilliseconds(Math.Max(1000, int.Parse(Opt("--delay-ms") ?? "1500", CultureInfo.InvariantCulture))),
    // Every monster picture is ~5,000 requests: --all-images raises the default cap accordingly.
    MaxRequests = int.Parse(Opt("--max-requests") ?? (Flag("--all-images") ? "6500" : "600"), CultureInfo.InvariantCulture),
    Offline = offline,
    Refresh = Flag("--refresh"),
};
var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DofusGoldBoss");
var output = Path.GetFullPath(Opt("--out") ?? Path.Combine(appData, "imports", "dofusdb.dofus-knowledge.json"));
var lang = Opt("--lang") ?? "fr";

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd(DofusDbClient.UserAgent);
var client = new DofusDbClient(http, options, Console.Out);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    Console.WriteLine($"Reading DofusDB ({(offline ? "cache only" : $"network, {options.Delay.TotalMilliseconds:0} ms between requests")})…");
    var dungeons = await client.GetAllAsync("dungeons", cts.Token);
    IReadOnlyList<System.Text.Json.JsonElement> mapPositions = [];
    try
    {
        mapPositions = await client.GetByIdsAsync("map-positions", DofusDbMapper.DungeonMapIds(dungeons).ToList(), cts.Token);
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
    {
        Console.WriteLine($"  warning: dungeon map coordinates unavailable ({ex.Message}); detection by coordinates disabled.");
    }

    var snapshot = new DofusDbSnapshot(
        dungeons,
        await client.GetAllAsync("monsters", cts.Token),
        await client.GetAllAsync("monster-races", cts.Token),
        await client.GetAllAsync("subareas", cts.Token),
        await client.GetAllAsync("areas", cts.Token),
        mapPositions);

    Func<int, string?>? imageFor = null;
    if (Flag("--images"))
    {
        var imagesDir = Path.GetFullPath(Opt("--images-dir") ?? Path.Combine(appData, "images"));
        var wanted = Flag("--all-images") ? null : KnownNames.Bosses();
        var template = Opt("--image-url") ?? "https://api.dofusdb.fr/img/monsters/{id}.png";
        var dungeonBosses = snapshot.Dungeons.SelectMany(d => d.TryGetProperty("bosses", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Array
            ? b.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.Number).Select(x => x.GetInt32()) : []).ToHashSet();
        var available = new HashSet<int>();
        var done = 0;
        Console.WriteLine($"Downloading pictures into {imagesDir} (already downloaded ones are skipped)…");
        foreach (var m in snapshot.Monsters)
        {
            if (DofusDbMapper.Int(m, "id") is not { } gameId || DofusDbMapper.ImageUrl(m, template) is not { } url) continue;
            // Default: every boss (DofusDB bosses, plus the app's bosses and wanted-poster targets matched by name).
            if (wanted is not null && !DofusDbMapper.IsBoss(m) && !dungeonBosses.Contains(gameId)
                && !DofusDbMapper.Names(m).Values.Any(n => wanted.Contains(DofusCompanion.Application.Matching.TextNormalizer.Normalize(n)))) continue;
            var target = Path.Combine(imagesDir, DofusDbMapper.ImageRelativePath(gameId).Replace('/', Path.DirectorySeparatorChar));
            if (await client.DownloadImageAsync(url, target, cts.Token)) available.Add(gameId);
            if (++done % 25 == 0) Console.WriteLine($"  pictures: {done} checked, {available.Count} available");
        }

        Console.WriteLine($"  pictures available: {available.Count}");
        imageFor = id => available.Contains(id) ? DofusDbMapper.ImageRelativePath(id) : null;
    }

    var result = DofusDbMapper.Map(snapshot, DateOnly.FromDateTime(DateTime.Today), lang, imageFor);
    var serializer = new KnowledgePackageSerializer();
    var json = serializer.Write(result.Package);
    await serializer.ReadAsync(json); // validate against the same schema the app uses before writing anything
    await serializer.WriteFileAsync(result.Package, output);

    Console.WriteLine($"Network requests: {client.NetworkRequests}");
    Console.WriteLine($"Entities: {result.Package.Entities.Count} ({string.Join(", ", result.Package.Entities.GroupBy(e => e.Entity.Type).Select(g => $"{g.Count()} {g.Key}"))})");
    foreach (var w in result.Warnings.Distinct().Take(20)) Console.WriteLine($"  warning: {w}");
    Console.WriteLine($"Written: {output}");
    Console.WriteLine("Start (or restart) Dofus Gold Boss - DGB: the package is imported automatically.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled. Already downloaded pages are kept in the cache.");
    return 130;
}
catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or PackageValidationException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
