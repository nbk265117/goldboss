using System.Text;
using System.Text.Json;
using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Imaging;
using DofusCompanion.Application.Matching;
using DofusCompanion.Application.Recognition;
using DofusCompanion.Domain;
using DofusCompanion.Infrastructure.Tests;
using DofusCompanion.Ocr.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace DofusCompanion.Ocr.Tests;

/// <summary>
/// End-to-end OCR measurements on the synthetic fixture set (scripts/generate-ocr-fixtures.ps1).
/// These tests need the Windows OCR engine with fr-FR installed; they report Skip-like failures with a
/// clear message otherwise. Metrics are written to TestResults/ocr-metrics.txt.
/// </summary>
public sealed class WindowsOcrTests(ITestOutputHelper output)
{
    private sealed record Case(string File, string Text, string[] Entities);

    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr");
    private static readonly WindowsOcrProvider Ocr = new();
    private static readonly WindowsImageDecoder Decoder = new();

    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNameCaseInsensitive = true };

    private static IReadOnlyList<Case> Cases() =>
        JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(Path.Combine(FixtureDir, "manifest.json")), ManifestJson)!;

    private static async Task<EntityMatcher> SeedMatcherAsync(TestHost host)
    {
        await host.Import.EnsureSeedAsync();
        return await host.Knowledge.GetMatcherAsync();
    }

    private static void RequireEngine()
    {
        var a = Ocr.GetAvailability();
        Assert.True(a.IsAvailable, "Windows OCR unavailable: " + a.Problem);
        Assert.True(a.Languages.Contains("fr-FR", StringComparer.OrdinalIgnoreCase), "fr-FR OCR language not installed");
    }

    [Fact]
    public void Engine_reports_availability_and_languages()
    {
        var a = Ocr.GetAvailability();
        output.WriteLine($"available={a.IsAvailable} languages={string.Join(",", a.Languages)} max={Ocr.MaxImageDimension}");
        Assert.True(a.IsAvailable || a.Problem is not null); // never silently unavailable
    }

    [Fact]
    public async Task Missing_language_gives_actionable_error()
    {
        RequireEngine();
        var ex = await Assert.ThrowsAsync<OcrException>(() => Ocr.RecognizeAsync(RasterImage.Blank(50, 50), "xx-XX", CancellationToken.None));
        Assert.Contains("xx-XX", ex.Message);
    }

    [Fact]
    public async Task Decoder_rejects_non_images_and_oversized_input()
    {
        await Assert.ThrowsAsync<ImageRejectedException>(() => Decoder.DecodeAsync(Encoding.UTF8.GetBytes("not an image at all"), CancellationToken.None));
        await Assert.ThrowsAsync<ImageRejectedException>(() => Decoder.DecodeAsync([], CancellationToken.None));
        // Valid PNG signature but truncated body.
        await Assert.ThrowsAsync<ImageRejectedException>(() => Decoder.DecodeAsync([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0], CancellationToken.None));
    }

    [Fact]
    public async Task Decoder_reads_fixture_png()
    {
        var img = await Decoder.DecodeAsync(await File.ReadAllBytesAsync(Path.Combine(FixtureDir, "clean-single.png")), CancellationToken.None);
        Assert.True(img.Width > 100 && img.Height > 10);
    }

    [Fact]
    public async Task Clean_text_is_read_exactly()
    {
        RequireEngine();
        var img = await Decoder.DecodeAsync(await File.ReadAllBytesAsync(Path.Combine(FixtureDir, "clean-single.png")), CancellationToken.None);
        var result = await Ocr.RecognizeAsync(img, "fr-FR", CancellationToken.None);
        Assert.Equal("kralamoure geant", TextNormalizer.Normalize(result.FullText));
        Assert.All(result.Lines, l => Assert.True(l.Bounds.Width > 0));
    }

    /// <summary>
    /// Runs the full recognition pipeline over every fixture and computes: character error rate (CER) on the
    /// best OCR attempt, entity precision and recall. Thresholds are deliberately conservative regression
    /// guards; the measured values are documented in docs/testing.md.
    /// </summary>
    [Fact]
    public async Task Pipeline_metrics_on_fixture_set()
    {
        RequireEngine();
        using var host = new TestHost();
        var matcher = await SeedMatcherAsync(host);
        var service = new RecognitionService(Ocr, NullLogger<RecognitionService>.Instance);

        int tp = 0, fp = 0, fn = 0;
        double cerSum = 0;
        var report = new StringBuilder();
        report.AppendLine("file | expected | found | outcome | CER | ms");
        foreach (var c in Cases())
        {
            var img = await Decoder.DecodeAsync(await File.ReadAllBytesAsync(Path.Combine(FixtureDir, c.File)), CancellationToken.None);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await service.RecognizeAsync(new RecognitionRequest { Image = img, Method = DetectionMethod.ImageImport, StopAtFirstConfident = false },
                matcher, null, CancellationToken.None);
            var ms = sw.ElapsedMilliseconds;

            // Only candidates the UI would show as acceptable count as "found": confident or above min score.
            var found = r.Candidates.Select(x => x.Entity.Id).ToHashSet();
            var expected = c.Entities.ToHashSet();
            tp += found.Intersect(expected).Count();
            fp += found.Except(expected).Count();
            fn += expected.Except(found).Count();

            var truth = TextNormalizer.Normalize(c.Text);
            var cer = r.Attempts.Where(a => a.Result is not null)
                .Select(a => (double)StringSimilarity.EditDistance(TextNormalizer.Normalize(a.Result!.FullText), truth) / Math.Max(1, truth.Length))
                .DefaultIfEmpty(1).Min();
            cerSum += cer;
            report.AppendLine($"{c.File} | {string.Join(",", expected)} | {string.Join(",", found)} | {r.Outcome} | {cer:P1} | {ms}");
        }

        var n = Cases().Count;
        var precision = tp + fp == 0 ? 1 : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 1 : (double)tp / (tp + fn);
        report.AppendLine($"cases={n} precision={precision:P1} recall={recall:P1} meanCER={cerSum / n:P1} (tp={tp} fp={fp} fn={fn})");
        output.WriteLine(report.ToString());
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "TestResults"));
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestResults", "ocr-metrics.txt"), report.ToString());

        Assert.True(precision >= 0.9, $"precision {precision:P1}\n{report}");
        Assert.True(recall >= 0.7, $"recall {recall:P1}\n{report}");
    }

    [Fact]
    public async Task Image_without_entity_never_yields_a_fabricated_match()
    {
        RequireEngine();
        using var host = new TestHost();
        var matcher = await SeedMatcherAsync(host);
        var img = await Decoder.DecodeAsync(await File.ReadAllBytesAsync(Path.Combine(FixtureDir, "no-entity.png")), CancellationToken.None);
        var r = await new RecognitionService(Ocr, NullLogger<RecognitionService>.Instance)
            .RecognizeAsync(new RecognitionRequest { Image = img, Method = DetectionMethod.ImageImport }, matcher, null, CancellationToken.None);
        Assert.Equal(MatchOutcome.NoMatch, r.Outcome);
        Assert.NotEmpty(r.AllOcrText); // text was read; it simply matches nothing
    }

    [Fact]
    public async Task Region_crop_limits_recognition()
    {
        RequireEngine();
        using var host = new TestHost();
        var matcher = await SeedMatcherAsync(host);
        var img = await Decoder.DecodeAsync(await File.ReadAllBytesAsync(Path.Combine(FixtureDir, "multi-names.png")), CancellationToken.None);
        // Lines are 24px * 1.7 apart starting at y=15: the first line only.
        var r = await new RecognitionService(Ocr, NullLogger<RecognitionService>.Instance).RecognizeAsync(
            new RecognitionRequest { Image = img, Method = DetectionMethod.ImageImport, Region = new PixelRect(0, 5, img.Width, 40) }, matcher, null, CancellationToken.None);
        output.WriteLine($"crop OCR text: [{r.AllOcrText}] fatal={r.FatalError}");
        Assert.Contains(r.Candidates, c => c.Entity.Id == "boss.tal-kasha");
        Assert.DoesNotContain(r.Candidates, c => c.Entity.Id == "boss.merkator");
        Assert.All(r.Candidates, c => Assert.InRange(c.SourceBounds!.Value.Y, 0, 60)); // mapped back to full-image coordinates
    }
}
