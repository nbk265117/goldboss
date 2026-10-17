using DofusCompanion.Application.Imaging;
using DofusCompanion.Application.Matching;
using DofusCompanion.Domain;

namespace DofusCompanion.Application.Recognition;

/// <summary>
/// Tiny grayscale thumbnail of a screen capture, used to skip OCR when nothing on screen changed
/// (no repeated OCR of an unchanged image).
/// </summary>
public sealed class ImageFingerprint
{
    public const int Width = 48;
    public const int Height = 27;
    private readonly byte[] _cells;

    private ImageFingerprint(byte[] cells) => _cells = cells;

    public static ImageFingerprint Of(RasterImage image)
    {
        var cells = new byte[Width * Height];
        var p = image.Pixels;
        for (var cy = 0; cy < Height; cy++)
        {
            for (var cx = 0; cx < Width; cx++)
            {
                // Sample a 3x3 grid inside each cell: cheap and stable against small noise.
                long sum = 0;
                for (var sy = 0; sy < 3; sy++)
                {
                    for (var sx = 0; sx < 3; sx++)
                    {
                        var x = Math.Min(image.Width - 1, (int)((cx + (sx + 0.5) / 3) * image.Width / Width));
                        var y = Math.Min(image.Height - 1, (int)((cy + (sy + 0.5) / 3) * image.Height / Height));
                        var i = (y * image.Width + x) * 4;
                        sum += ImagePreprocessor.Luma(p[i], p[i + 1], p[i + 2]);
                    }
                }

                cells[cy * Width + cx] = (byte)(sum / 9);
            }
        }

        return new ImageFingerprint(cells);
    }

    /// <summary>Mean absolute luminance difference (0 = identical, 255 = inverted).</summary>
    public double DistanceTo(ImageFingerprint other)
    {
        long d = 0;
        for (var i = 0; i < _cells.Length; i++) d += Math.Abs(_cells[i] - other._cells[i]);
        return (double)d / _cells.Length;
    }
}

/// <summary>Map coordinates as shown at the top left of the game ("-82,-48", "[-82, -48]", "-82 , -48").</summary>
public static partial class MapCoordinates
{
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\d.,])(-?\d{1,3})\s*[,;]\s*(-?\d{1,3})(?![\d.,])(?!\s*%)")]
    private static partial System.Text.RegularExpressions.Regex Pattern();

    public static IReadOnlyList<(int X, int Y)> Find(string text) => Pattern().Matches(text)
        .Select(m => (X: int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), Y: int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))
        .Where(c => Math.Abs(c.X) <= 200 && Math.Abs(c.Y) <= 200)
        .Distinct()
        .ToList();
}

public enum AutoDetectAction
{
    /// <summary>Nothing reliable found, or the same entity is already shown.</summary>
    None,
    /// <summary>Seen once; waiting for a second consistent scan before switching.</summary>
    Pending,
    /// <summary>Show this entity.</summary>
    Show,
}

public sealed record AutoDetectDecision(AutoDetectAction Action, GameEntity? Entity, double Score, IReadOnlyList<GameEntity> MonstersSeen);

/// <summary>
/// Chooses what automatic detection should display from a recognition report. Priority: boss, dungeon,
/// monster, area/map. A new entity is shown only when two consecutive scans agree (an exact boss name is
/// shown at once), so a name glimpsed once in the chat does not hijack the overlay.
/// </summary>
public sealed partial class AutoDetectDecider(double minScore = 0.88)
{
    private string? _pendingId;
    private int _pendingHits;

    public string? CurrentId { get; private set; }

    public static int Priority(EntityType t) => t switch
    {
        EntityType.Boss => 0,
        EntityType.Dungeon => 1,
        EntityType.Monster => 2,
        EntityType.Area or EntityType.Map => 3,
        _ => 9,
    };

    /// <summary>
    /// One-shot choice (detect button): the best reliable candidate by priority, no confirmation scan. When a
    /// dungeon/area is recognised too, <paramref name="bossesIn"/> (place id → its bosses) keeps only the bosses of
    /// that place: a boss name read in the chat must not win over the dungeon the player is in. If none of the
    /// recognised bosses belongs to it, the place itself is shown (its sheet lists its bosses).
    /// </summary>
    public static AutoDetectDecision Pick(IReadOnlyList<RecognizedCandidate> candidates, double minScore = 0.88,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? bossesIn = null)
    {
        var reliable = WithoutChat(candidates)
            .Where(c => c.Score >= minScore && Priority(c.Entity.Type) < 9 && !IsShortWordInSentence(c))
            .ToList();
        if (bossesIn is { Count: > 0 })
        {
            var local = bossesIn.Values.SelectMany(b => b).ToHashSet(StringComparer.Ordinal);
            reliable.RemoveAll(c => c.Entity.Type == EntityType.Boss && !local.Contains(c.Entity.Id));
        }

        var monsters = reliable.Where(c => c.Entity.Type is EntityType.Monster or EntityType.Boss).Select(c => c.Entity).DistinctBy(e => e.Id).ToList();
        // A name alone on its line (the map name at the top left, a hovered monster's tooltip) beats a name found
        // inside a sentence.
        var best = reliable.OrderBy(c => Priority(c.Entity.Type)).ThenByDescending(IsWholeLine).ThenByDescending(c => c.Score).FirstOrDefault();
        return new AutoDetectDecision(best is null ? AutoDetectAction.None : AutoDetectAction.Show, best?.Entity, best?.Score ?? 0, monsters);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\W{0,3}\[?\d{1,2}[:h]\d{2}\]")]
    private static partial System.Text.RegularExpressions.Regex ChatTimestamp();

    /// <summary>A chat message ("[16:32] Les portes de l'Antre du Kralamoure Géant sont ouvertes."): not what is on the map.</summary>
    public static bool IsChatLine(string ocrText) => ChatTimestamp().IsMatch(ocrText.TrimStart());

    public static IEnumerable<RecognizedCandidate> WithoutChat(IEnumerable<RecognizedCandidate> candidates) =>
        candidates.Where(c => !IsChatLine(c.OcrText));

    /// <summary>
    /// A very short name ("Dé") found inside a sentence ("Songe de : …") is an ordinary word, not that monster;
    /// it counts only when it stands alone on its line (a tooltip).
    /// </summary>
    private static bool IsShortWordInSentence(RecognizedCandidate c) =>
        TextNormalizer.Normalize(c.MatchedName).Length < 4 && !IsWholeLine(c);

    private static bool IsWholeLine(RecognizedCandidate c) =>
        TextNormalizer.Normalize(c.OcrText).Length <= TextNormalizer.Normalize(c.MatchedName).Length + 4;

    public void Reset()
    {
        CurrentId = null;
        _pendingId = null;
        _pendingHits = 0;
    }

    public AutoDetectDecision Decide(IReadOnlyList<RecognizedCandidate> candidates)
    {
        var reliable = candidates.Where(c => c.Score >= minScore && Priority(c.Entity.Type) < 9).ToList();
        var monsters = reliable.Where(c => c.Entity.Type is EntityType.Monster or EntityType.Boss).Select(c => c.Entity).DistinctBy(e => e.Id).ToList();
        var best = reliable
            .OrderBy(c => Priority(c.Entity.Type))
            .ThenByDescending(c => c.Score)
            .FirstOrDefault();

        if (best is null)
        {
            _pendingId = null;
            _pendingHits = 0;
            return new AutoDetectDecision(AutoDetectAction.None, null, 0, monsters);
        }

        if (best.Entity.Id == CurrentId)
        {
            _pendingId = null;
            _pendingHits = 0;
            return new AutoDetectDecision(AutoDetectAction.None, best.Entity, best.Score, monsters);
        }

        _pendingHits = best.Entity.Id == _pendingId ? _pendingHits + 1 : 1;
        _pendingId = best.Entity.Id;
        var exactBoss = best.Entity.Type == EntityType.Boss && best.Score >= 0.999;
        if (_pendingHits >= 2 || exactBoss)
        {
            CurrentId = best.Entity.Id;
            _pendingId = null;
            _pendingHits = 0;
            return new AutoDetectDecision(AutoDetectAction.Show, best.Entity, best.Score, monsters);
        }

        return new AutoDetectDecision(AutoDetectAction.Pending, best.Entity, best.Score, monsters);
    }
}
