using System.Net;
using System.Text.Json;

namespace DofusCompanion.DofusDbImporter;

public sealed record ClientOptions
{
    public Uri BaseUri { get; init; } = new("https://api.dofusdb.fr/");
    public string CacheDirectory { get; init; } = "dofusdb-cache";
    /// <summary>Pause between two network requests (politeness). Cached pages cost nothing.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(1500);
    public int PageSize { get; init; } = 50;
    /// <summary>Hard cap on network requests per run.</summary>
    public int MaxRequests { get; init; } = 600;
    public bool Offline { get; init; }
    public bool Refresh { get; init; }
}

/// <summary>
/// Polite, cached, paginated reader for the DofusDB public API. Sequential requests only, fixed delay,
/// honours HTTP 429 Retry-After, stops on 401/403, and keeps every page on disk so a re-run does not hit
/// the site again (use --refresh to re-download).
/// </summary>
public sealed class DofusDbClient(HttpClient http, ClientOptions options, TextWriter log)
{
    public const string UserAgent = "DofusGoldBoss-Importer/0.4 (user-run, low volume)";
    private int _requests;

    public int NetworkRequests => _requests;

    public async Task<IReadOnlyList<JsonElement>> GetAllAsync(string endpoint, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var skip = 0;
        int? total = null;
        while (total is null || skip < total)
        {
            using var doc = JsonDocument.Parse(await GetPageAsync(endpoint, skip, ct));
            var root = doc.RootElement;
            total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Unexpected response shape for {endpoint} (no 'data' array).");
            var count = 0;
            foreach (var e in data.EnumerateArray())
            {
                all.Add(e.Clone());
                count++;
            }

            if (count == 0) break; // defensive: never loop forever on an inconsistent total
            skip += count;
            log.WriteLine($"  {endpoint}: {all.Count}/{total}");
        }

        return all;
    }

    /// <summary>
    /// Records of <paramref name="endpoint"/> with the given ids, 50 per request (<c>id[$in][]=…</c>), each batch cached
    /// like the pages. Used for the map positions of dungeon rooms only (a few dozen requests).
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> GetByIdsAsync(string endpoint, IReadOnlyList<int> ids, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var sorted = ids.Distinct().Order().ToList();
        for (var i = 0; i < sorted.Count; i += options.PageSize)
        {
            var batch = sorted.Skip(i).Take(options.PageSize).ToList();
            var query = $"$limit={batch.Count}&" + string.Join("&", batch.Select(id => $"id[$in][]={id}"));
            var cacheFile = Path.Combine(options.CacheDirectory, $"{endpoint}-ids-{batch[0]}-{batch[^1]}-{batch.Count}.json");
            using var doc = JsonDocument.Parse(await GetCachedAsync(cacheFile, new Uri(options.BaseUri, $"{endpoint}?{query}"), ct));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var e in data.EnumerateArray()) all.Add(e.Clone());
            log.WriteLine($"  {endpoint}: {Math.Min(i + batch.Count, sorted.Count)}/{sorted.Count}");
        }

        return all;
    }

    public const long MaxImageBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Downloads one picture to <paramref name="target"/> unless it already exists. Same politeness rules as API
    /// pages (delay, request cap, 403 stop). Only real images (PNG/JPEG/GIF/WebP signature) up to 5 MB are kept.
    /// Returns true if the file is present afterwards.
    /// </summary>
    public async Task<bool> DownloadImageAsync(Uri source, string target, CancellationToken ct)
    {
        if (File.Exists(target) && !options.Refresh) return true;
        if (options.Offline) return File.Exists(target);
        if (_requests >= options.MaxRequests) throw new InvalidOperationException($"Request cap reached ({options.MaxRequests}). Re-run to continue: downloaded pictures are kept.");

        if (_requests > 0) await Task.Delay(options.Delay, ct);
        _requests++;
        using var response = await http.GetAsync(source, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException($"Access refused by the site ({(int)response.StatusCode}). Stopping: do not work around it.");
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxImageBytes) return false;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes || !LooksLikeImage(bytes)) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target + ".tmp", bytes, ct);
        File.Move(target + ".tmp", target, overwrite: true);
        return true;
    }

    internal static bool LooksLikeImage(ReadOnlySpan<byte> d) =>
        (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)                     // PNG
        || (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)                                  // JPEG
        || (d.Length > 4 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46)                                  // GIF
        || (d.Length > 12 && d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46 && d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50); // WebP

    private async Task<string> GetPageAsync(string endpoint, int skip, CancellationToken ct)
    {
        Directory.CreateDirectory(options.CacheDirectory);
        var cacheFile = Path.Combine(options.CacheDirectory, $"{endpoint}-{skip:D6}.json");
        return await GetCachedAsync(cacheFile, new Uri(options.BaseUri, $"{endpoint}?$limit={options.PageSize}&$skip={skip}"), ct);
    }

    private async Task<string> GetCachedAsync(string cacheFile, Uri uri, CancellationToken ct)
    {
        Directory.CreateDirectory(options.CacheDirectory);
        if (!options.Refresh && File.Exists(cacheFile)) return await File.ReadAllTextAsync(cacheFile, ct);
        if (options.Offline) throw new InvalidOperationException($"Offline mode: {cacheFile} is not cached.");
        if (_requests >= options.MaxRequests) throw new InvalidOperationException($"Request cap reached ({options.MaxRequests}). Re-run to continue from the cache.");

        for (var attempt = 1; ; attempt++)
        {
            if (_requests > 0) await Task.Delay(options.Delay, ct);
            _requests++;
            using var response = await http.GetAsync(uri, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt <= 3)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30 * attempt);
                log.WriteLine($"  429 Too Many Requests: waiting {wait.TotalSeconds:0} s");
                await Task.Delay(wait, ct);
                continue;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new InvalidOperationException($"Access refused by the site ({(int)response.StatusCode}). Stopping: do not work around it.");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            await File.WriteAllTextAsync(cacheFile, body, ct);
            return body;
        }
    }
}
