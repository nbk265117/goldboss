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
    public const string UserAgent = "DofusCompanion-Importer/0.1 (user-run, low volume)";
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

    private async Task<string> GetPageAsync(string endpoint, int skip, CancellationToken ct)
    {
        Directory.CreateDirectory(options.CacheDirectory);
        var cacheFile = Path.Combine(options.CacheDirectory, $"{endpoint}-{skip:D6}.json");
        if (!options.Refresh && File.Exists(cacheFile)) return await File.ReadAllTextAsync(cacheFile, ct);
        if (options.Offline) throw new InvalidOperationException($"Offline mode: {cacheFile} is not cached.");
        if (_requests >= options.MaxRequests) throw new InvalidOperationException($"Request cap reached ({options.MaxRequests}). Re-run to continue from the cache.");

        var uri = new Uri(options.BaseUri, $"{endpoint}?$limit={options.PageSize}&$skip={skip}");
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
