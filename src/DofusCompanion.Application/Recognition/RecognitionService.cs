using System.Diagnostics;
using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Imaging;
using DofusCompanion.Application.Matching;
using DofusCompanion.Domain;
using Microsoft.Extensions.Logging;

namespace DofusCompanion.Application.Recognition;

public sealed record RecognitionRequest
{
    public required RasterImage Image { get; init; }
    public required DetectionMethod Method { get; init; }
    /// <summary>Optional crop in source-image pixels.</summary>
    public PixelRect? Region { get; init; }
    public string Language { get; init; } = "fr-FR";
    public IReadOnlyList<PreprocessVariant>? Variants { get; init; }
    /// <summary>When false, every variant runs even after a confident match (diagnostics/benchmarks).</summary>
    public bool StopAtFirstConfident { get; init; } = true;
}

public sealed record OcrAttempt(string Variant, OcrResult? Result, string? Error, TimeSpan Duration, double ScaleFactor);

/// <summary>A recognized entity with the evidence that produced it.</summary>
public sealed record RecognizedCandidate(
    GameEntity Entity,
    double Score,
    string MatchedName,
    string OcrText,
    PixelRect? SourceBounds,
    string Variant);

public sealed record RecognitionReport
{
    public required Guid ObservationId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required DetectionMethod Method { get; init; }
    public required IReadOnlyList<OcrAttempt> Attempts { get; init; }
    /// <summary>All entities found, best first.</summary>
    public required IReadOnlyList<RecognizedCandidate> Candidates { get; init; }
    public required MatchOutcome Outcome { get; init; }
    /// <summary>Set when OCR could not run at all (engine missing, language missing...).</summary>
    public string? FatalError { get; init; }

    public string AllOcrText => string.Join(Environment.NewLine + "---" + Environment.NewLine,
        Attempts.Where(a => a.Result is not null).Select(a => $"[{a.Variant}]" + Environment.NewLine + a.Result!.FullText));
}

/// <summary>
/// Image → preprocessing variants → OCR → entity matching. Never invents a result: if nothing matches,
/// the report says so; if several entities are plausible, the outcome is Ambiguous and the UI must ask.
/// </summary>
public sealed class RecognitionService(IOcrProvider ocr, ILogger<RecognitionService> logger)
{
    internal const int PadMargin = 24;

    public async Task<RecognitionReport> RecognizeAsync(RecognitionRequest request, EntityMatcher matcher,
        IProgress<string>? progress, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        var availability = ocr.GetAvailability();
        if (!availability.IsAvailable)
            return Fatal(request, started, availability.Problem ?? "OCR engine unavailable.");
        if (!availability.Languages.Contains(request.Language, StringComparer.OrdinalIgnoreCase))
            return Fatal(request, started, $"OCR language '{request.Language}' is not installed. Installed: {string.Join(", ", availability.Languages)}.");

        var source = request.Region is { } region ? ImagePreprocessor.Crop(request.Image, region) : request.Image;
        var offsetX = request.Region?.X ?? 0;
        var offsetY = request.Region?.Y ?? 0;

        // Tight crops and small images: text touching the border is often not detected at all (measured on
        // the fixture set), so give the engine a margin and shift coordinates back afterwards.
        if (request.Region is not null || Math.Min(source.Width, source.Height) < 200)
        {
            source = ImagePreprocessor.Pad(source, PadMargin);
            offsetX -= PadMargin;
            offsetY -= PadMargin;
        }

        var attempts = new List<OcrAttempt>();
        var best = new Dictionary<string, RecognizedCandidate>(StringComparer.Ordinal);
        var variants = request.Variants ?? PreprocessVariant.Defaults;

        foreach (var variant in variants)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(variant.Name);
            var sw = Stopwatch.StartNew();

            // CPU-bound pixel work off the caller's (UI) thread.
            var prepared = await Task.Run(() => ImagePreprocessor.Apply(source, variant, ocr.MaxImageDimension), ct).ConfigureAwait(false);
            var scale = (double)prepared.Width / source.Width;

            OcrResult result;
            try
            {
                result = await ocr.RecognizeAsync(prepared, request.Language, ct).ConfigureAwait(false);
            }
            catch (OcrException ex)
            {
                logger.LogWarning(ex, "OCR failed for variant {Variant}", variant.Name);
                attempts.Add(new OcrAttempt(variant.Name, null, ex.Message, sw.Elapsed, scale));
                continue;
            }

            attempts.Add(new OcrAttempt(variant.Name, result, null, sw.Elapsed, scale));

            foreach (var line in result.Lines)
            {
                foreach (var c in matcher.FindInText([line.Text]))
                {
                    var bounds = new PixelRect(
                        Math.Max(0, (int)(line.Bounds.X / scale) + offsetX),
                        Math.Max(0, (int)(line.Bounds.Y / scale) + offsetY),
                        (int)Math.Ceiling(line.Bounds.Width / scale),
                        (int)Math.Ceiling(line.Bounds.Height / scale));
                    if (!best.TryGetValue(c.Entity.Id, out var existing) || existing.Score < c.Score)
                        best[c.Entity.Id] = new RecognizedCandidate(c.Entity, c.Score, c.MatchedName, line.Text, bounds, variant.Name);
                }
            }

            logger.LogInformation("OCR variant {Variant}: {Lines} lines, {Candidates} candidates so far, {Ms} ms",
                variant.Name, result.Lines.Count, best.Count, sw.ElapsedMilliseconds);

            if (request.StopAtFirstConfident && Classify(matcher, best.Values).Outcome == MatchOutcome.Confident)
                break;
        }

        var ranked = best.Values.OrderByDescending(c => c.Score).ThenBy(c => c.Entity.CanonicalName, StringComparer.CurrentCulture).ToList();
        var outcome = Classify(matcher, ranked).Outcome;
        string? fatal = attempts.Count > 0 && attempts.All(a => a.Result is null) ? attempts[^1].Error : null;

        return new RecognitionReport
        {
            ObservationId = Guid.NewGuid(),
            Timestamp = started,
            Method = request.Method,
            Attempts = attempts,
            Candidates = ranked,
            Outcome = outcome,
            FatalError = fatal,
        };
    }

    /// <summary>
    /// Several distinct monsters can legitimately be on screen, so "ambiguous" here means the best two
    /// candidates are near-identical in score (likely the same text matching two names).
    /// </summary>
    private static MatchResult Classify(EntityMatcher matcher, IEnumerable<RecognizedCandidate> candidates) =>
        matcher.Classify(candidates.Select(c => new MatchCandidate(c.Entity, c.Score, c.MatchedName, c.OcrText))
            // Candidates found on different lines are different on-screen evidence, not competing readings.
            .GroupBy(c => c.MatchedText)
            .OrderByDescending(g => g.Max(c => c.Score))
            .FirstOrDefault() ?? Enumerable.Empty<MatchCandidate>());

    private static RecognitionReport Fatal(RecognitionRequest request, DateTimeOffset started, string error) => new()
    {
        ObservationId = Guid.NewGuid(),
        Timestamp = started,
        Method = request.Method,
        Attempts = [],
        Candidates = [],
        Outcome = MatchOutcome.NoMatch,
        FatalError = error,
    };
}
