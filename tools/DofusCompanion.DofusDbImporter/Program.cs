using System.Globalization;
using DofusCompanion.DofusDbImporter;
using DofusCompanion.Infrastructure.Knowledge;

// dofusdb-import — builds a Dofus Companion knowledge package (monsters, bosses, dungeons, areas,
// families) from the DofusDB public API. Run it YOURSELF; the application itself never goes online.

const string Usage = """
Usage: dofusdb-import --accept-terms [options]

  --accept-terms        Required for network access. Confirms you checked DofusDB's conditions of use
                        (and Ankama's CGU) and accept responsibility for this personal, low-volume use.
  --out <file.json>     Output package (default: dofusdb.dofus-knowledge.json)
  --cache <dir>         Cache folder for raw API pages (default: dofusdb-cache)
  --delay-ms <n>        Pause between requests, minimum 1000 (default: 1500)
  --max-requests <n>    Network request cap for this run (default: 600)
  --offline             Use only the cache, never the network
  --refresh             Re-download pages even if cached
  --lang <fr|en|...>    Language of canonical names (default: fr)
  --images              Also download monster/boss pictures (personal use) into the app's images folder
  --images-dir <dir>    Pictures folder (default: %LOCALAPPDATA%\DofusCompanion\images)
  --image-url <tpl>     Picture URL template when a record has none (default: https://api.dofusdb.fr/img/monsters/{id}.png)

Then import the file in Dofus Companion: Connaissances › Importer un paquet… (preview before applying).
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
    MaxRequests = int.Parse(Opt("--max-requests") ?? "600", CultureInfo.InvariantCulture),
    Offline = offline,
    Refresh = Flag("--refresh"),
};
var output = Path.GetFullPath(Opt("--out") ?? "dofusdb.dofus-knowledge.json");
var lang = Opt("--lang") ?? "fr";

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd(DofusDbClient.UserAgent);
var client = new DofusDbClient(http, options, Console.Out);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    Console.WriteLine($"Reading DofusDB ({(offline ? "cache only" : $"network, {options.Delay.TotalMilliseconds:0} ms between requests")})…");
    var snapshot = new DofusDbSnapshot(
        await client.GetAllAsync("dungeons", cts.Token),
        await client.GetAllAsync("monsters", cts.Token),
        await client.GetAllAsync("monster-races", cts.Token),
        await client.GetAllAsync("subareas", cts.Token),
        await client.GetAllAsync("areas", cts.Token));

    Func<int, string?>? imageFor = null;
    if (Flag("--images"))
    {
        var imagesDir = Path.GetFullPath(Opt("--images-dir") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DofusCompanion", "images"));
        var template = Opt("--image-url") ?? "https://api.dofusdb.fr/img/monsters/{id}.png";
        var available = new HashSet<int>();
        var done = 0;
        Console.WriteLine($"Downloading pictures into {imagesDir} (already downloaded ones are skipped)…");
        foreach (var m in snapshot.Monsters)
        {
            if (DofusDbMapper.Int(m, "id") is not { } gameId || DofusDbMapper.ImageUrl(m, template) is not { } url) continue;
            var target = Path.Combine(imagesDir, DofusDbMapper.ImageRelativePath(gameId).Replace('/', Path.DirectorySeparatorChar));
            if (await client.DownloadImageAsync(url, target, cts.Token)) available.Add(gameId);
            if (++done % 100 == 0) Console.WriteLine($"  pictures: {done}/{snapshot.Monsters.Count} checked, {available.Count} available");
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
