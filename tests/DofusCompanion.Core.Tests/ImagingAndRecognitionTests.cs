using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Imaging;
using DofusCompanion.Application.Matching;
using DofusCompanion.Application.Recognition;
using DofusCompanion.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace DofusCompanion.Core.Tests;

public class ImagePreprocessorTests
{
    [Fact]
    public void Resize_doubles_dimensions()
    {
        var img = ImagePreprocessor.Resize(RasterImage.Blank(10, 5), 2.0);
        Assert.Equal((20, 10), (img.Width, img.Height));
    }

    [Fact]
    public void Apply_respects_engine_max_dimension()
    {
        var img = ImagePreprocessor.Apply(RasterImage.Blank(1000, 500), new PreprocessVariant("x", 4.0, []), maxDimension: 1500);
        Assert.True(img.Width <= 1500 && img.Height <= 1500);
    }

    [Fact]
    public void Invert_and_grayscale()
    {
        var img = new RasterImage(1, 1, [10, 20, 30, 255]);
        ImagePreprocessor.Grayscale(img);
        Assert.Equal(img.Pixels[0], img.Pixels[1]);
        var before = img.Pixels[0];
        ImagePreprocessor.Invert(img);
        Assert.Equal(255 - before, img.Pixels[0]);
        Assert.Equal(255, img.Pixels[3]); // alpha untouched
    }

    [Fact]
    public void Otsu_separates_bimodal_image()
    {
        var px = new byte[4 * 100];
        for (var i = 0; i < 100; i++)
        {
            var v = (byte)(i < 50 ? 40 : 200);
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v;
            px[i * 4 + 3] = 255;
        }

        var img = new RasterImage(10, 10, px);
        ImagePreprocessor.OtsuThreshold(img);
        Assert.Equal(0, img.Pixels[0]);
        Assert.Equal(255, img.Pixels[99 * 4]);
    }

    [Fact]
    public void Pad_adds_border_with_edge_colour()
    {
        var img = ImagePreprocessor.Pad(new RasterImage(2, 1, [10, 20, 30, 255, 10, 20, 30, 255]), 3);
        Assert.Equal((8, 7), (img.Width, img.Height));
        Assert.Equal([10, 20, 30, 255], img.Pixels[..4]);
    }

    [Fact]
    public void Crop_is_clamped_to_image()
    {
        var img = ImagePreprocessor.Crop(RasterImage.Blank(10, 10), new PixelRect(8, 8, 50, 50));
        Assert.Equal((2, 2), (img.Width, img.Height));
    }

    [Fact]
    public void Oversized_image_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RasterImage(10_000, 10_000, new byte[4]));

    [Fact]
    public void Mismatched_buffer_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new RasterImage(2, 2, new byte[3]));
}

public class RecognitionServiceTests
{
    /// <summary>Test double returning scripted text per variant. Never used in production.</summary>
    private sealed class ScriptedOcr(Func<int, IReadOnlyList<string>> linesForCall, bool available = true) : IOcrProvider
    {
        public int Calls { get; private set; }
        public string Name => "scripted";
        public int MaxImageDimension => 4000;
        public OcrAvailability GetAvailability() => available ? new(true, ["fr-FR"], null) : new(false, [], "missing engine");

        public Task<OcrResult> RecognizeAsync(RasterImage image, string language, CancellationToken ct)
        {
            var lines = linesForCall(Calls++);
            return Task.FromResult(new OcrResult(language,
                lines.Select((l, i) => new OcrLine(l, new PixelRect(0, i * 20 * image.Width / 100, image.Width, 20), [])).ToList(), TimeSpan.Zero));
        }
    }

    private static readonly EntityMatcher Matcher = new([TestData.Entity("boss.alpha", "Gardien Alpha"), TestData.Entity("boss.beta", "Gardien Beta")]);

    // 400px: large enough that the small-image padding does not apply (tested separately).
    private static RecognitionRequest Request(string lang = "fr-FR") => new() { Image = RasterImage.Blank(400, 400), Method = DetectionMethod.ImageImport, Language = lang };

    [Fact]
    public async Task Stops_at_first_confident_variant()
    {
        var ocr = new ScriptedOcr(_ => ["Gardien Alpha"]);
        var report = await new RecognitionService(ocr, NullLogger<RecognitionService>.Instance).RecognizeAsync(Request(), Matcher, null, CancellationToken.None);
        Assert.Equal(MatchOutcome.Confident, report.Outcome);
        Assert.Equal(1, ocr.Calls);
        Assert.Equal("boss.alpha", report.Candidates[0].Entity.Id);
    }

    [Fact]
    public async Task Tries_more_variants_when_first_is_poor()
    {
        var ocr = new ScriptedOcr(call => call == 0 ? ["G@rd!en"] : ["Gardien Beta"]);
        var report = await new RecognitionService(ocr, NullLogger<RecognitionService>.Instance).RecognizeAsync(Request(), Matcher, null, CancellationToken.None);
        Assert.True(ocr.Calls >= 2);
        Assert.Equal("boss.beta", report.Candidates[0].Entity.Id);
    }

    [Fact]
    public async Task No_text_yields_no_match_never_a_fabricated_result()
    {
        var ocr = new ScriptedOcr(_ => ["PA 6 PM 3", "Tour 4"]);
        var report = await new RecognitionService(ocr, NullLogger<RecognitionService>.Instance).RecognizeAsync(Request(), Matcher, null, CancellationToken.None);
        Assert.Equal(MatchOutcome.NoMatch, report.Outcome);
        Assert.Empty(report.Candidates);
        Assert.Equal(PreprocessVariant.Defaults.Count, ocr.Calls);
    }

    [Fact]
    public async Task Missing_engine_is_a_clear_error()
    {
        var report = await new RecognitionService(new ScriptedOcr(_ => [], available: false), NullLogger<RecognitionService>.Instance)
            .RecognizeAsync(Request(), Matcher, null, CancellationToken.None);
        Assert.Equal("missing engine", report.FatalError);
    }

    [Fact]
    public async Task Missing_language_is_a_clear_error()
    {
        var report = await new RecognitionService(new ScriptedOcr(_ => []), NullLogger<RecognitionService>.Instance)
            .RecognizeAsync(Request("ja-JP"), Matcher, null, CancellationToken.None);
        Assert.Contains("ja-JP", report.FatalError);
    }

    [Fact]
    public async Task Bounds_are_mapped_back_to_source_coordinates()
    {
        // Variant 2 upscales x2; the scripted line sits at y = 20% of the processed image width.
        var ocr = new ScriptedOcr(call => call == 0 ? [] : ["x", "Gardien Alpha"]);
        var report = await new RecognitionService(ocr, NullLogger<RecognitionService>.Instance).RecognizeAsync(Request(), Matcher, null, CancellationToken.None);
        var bounds = report.Candidates[0].SourceBounds!.Value;
        Assert.Equal(80, bounds.Y); // 160px in the 800px image -> 80px in the 400px source
        Assert.Equal(400, bounds.Width);
    }

    [Fact]
    public async Task Region_is_padded_and_mapped_back_to_full_image()
    {
        var seen = new List<(int W, int H)>();
        var ocr = new ScriptedOcr(_ => ["Gardien Alpha"]);
        var spy = new SpyOcr(ocr, seen);
        var request = Request() with { Region = new PixelRect(100, 50, 200, 30) };
        var report = await new RecognitionService(spy, NullLogger<RecognitionService>.Instance).RecognizeAsync(request, Matcher, null, CancellationToken.None);
        Assert.Equal((200 + 48, 30 + 48), seen[0]); // 24px margin on each side
        Assert.True(report.Candidates[0].SourceBounds!.Value.X >= 76); // never outside the image, offset by region minus margin
    }

    private sealed class SpyOcr(IOcrProvider inner, List<(int W, int H)> seen) : IOcrProvider
    {
        public string Name => inner.Name;
        public int MaxImageDimension => inner.MaxImageDimension;
        public OcrAvailability GetAvailability() => inner.GetAvailability();
        public Task<OcrResult> RecognizeAsync(RasterImage image, string language, CancellationToken ct)
        {
            seen.Add((image.Width, image.Height));
            return inner.RecognizeAsync(image, language, ct);
        }
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RecognitionService(new ScriptedOcr(_ => []), NullLogger<RecognitionService>.Instance).RecognizeAsync(Request(), Matcher, null, cts.Token));
    }
}
