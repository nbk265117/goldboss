using System.Diagnostics;
using DofusCompanion.App.Localization;
using DofusCompanion.Application;
using DofusCompanion.Application.Imaging;
using DofusCompanion.Application.Recognition;
using DofusCompanion.Application.Settings;
using DofusCompanion.Domain;
using Microsoft.Extensions.Logging;

namespace DofusCompanion.App.Services;

/// <summary>
/// One-click detection ("Détecter" button of the overlay): captures the Dofus screen (or the chosen zone),
/// reads the text locally and directly shows the boss / dungeon / monster / zone it recognises, by priority.
/// It runs only when the player clicks; it is read-only and never sends anything to the game.
/// </summary>
public sealed class AutoDetectService(ISettingsStore settings, RecognitionService recognition, KnowledgeService knowledge,
    StatusService status, FightContext fight, ILogger<AutoDetectService> logger)
{
    /// <summary>Shows a detected entity (wired to the overlay by the shell).</summary>
    public Func<string, Task>? Show { get; set; }

    /// <summary>Hides the overlay from the capture for its duration (wired by the shell).</summary>
    public Func<Task<IDisposable?>>? BeforeCapture { get; set; }

    /// <summary>Monitor used when the Dofus window is not in front (the overlay's one).</summary>
    public Func<RasterImage>? CaptureFallback { get; set; }

    public bool IsBusy { get; private set; }

    public async Task<GameEntity?> DetectAsync(CancellationToken ct = default)
    {
        if (IsBusy) return null;
        if (!settings.Current.ScreenCaptureEnabled)
        {
            status.Error(Loc.Instance["AutoDetect.NeedsCapture"]);
            return null;
        }

        IsBusy = true;
        status.Info(Loc.Instance["AutoDetect.Running"]);
        try
        {
            // Clicking "Détecter" puts the overlay in front: look for the Dofus window itself in that case.
            var dofus = DofusWindows.Foreground();
            if (dofus == 0) dofus = DofusWindows.FirstVisible();
            var region = settings.Current.AutoDetectRegion is { Width: > 8, Height: > 8 } zone ? zone : null;
            RasterImage image;
            using (BeforeCapture is null ? null : await BeforeCapture())
            {
                // A chosen zone is relative to the whole monitor; otherwise only the game window is read.
                image = dofus == 0 ? CaptureFallback?.Invoke() ?? ScreenCaptureService.CapturePrimary()
                    : region is null ? ScreenCaptureService.CaptureWindowArea(dofus)
                    : ScreenCaptureService.CaptureMonitorOf(dofus);
            }

            if (region is { } r && r.X + r.Width <= image.Width && r.Y + r.Height <= image.Height)
                image = ImagePreprocessor.Crop(image, new PixelRect(r.X, r.Y, r.Width, r.Height));

            var sw = Stopwatch.StartNew();
            var report = await recognition.RecognizeAsync(new RecognitionRequest
            {
                Image = image,
                Method = DetectionMethod.ScreenCapture,
                Language = settings.Current.OcrLanguage,
            }, await knowledge.GetMatcherAsync(ct), null, ct);

            // Map coordinates at the top left of the game ("-82,-48") are the most reliable hint of the dungeon.
            var candidates = AutoDetectDecider.WithoutChat(report.Candidates).ToList(); // chat lines are not the map
            foreach (var (x, y) in MapCoordinates.Find(report.AllOcrText))
                foreach (var d in await knowledge.GetDungeonsAtAsync(x, y, ct))
                    if (candidates.All(c => c.Entity.Id != d.Id))
                        candidates.Insert(0, new RecognizedCandidate(d, 1.0, d.CanonicalName, $"{x},{y}", null, "coordinates"));

            // Places recognised on screen (dungeon / zone) and their bosses: used to ignore a boss read in the chat.
            var bossesIn = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            foreach (var place in candidates.Where(c => c.Score >= 0.88 && c.Entity.Type is EntityType.Dungeon or EntityType.Area or EntityType.Map)
                         .Select(c => c.Entity.Id).Distinct())
            {
                var bosses = await knowledge.GetBossIdsInAsync(place, ct);
                if (bosses.Count > 0) bossesIn[place] = bosses;
            }

            var pick = AutoDetectDecider.Pick(candidates, bossesIn: bossesIn);
            logger.LogInformation("Detection read: {Candidates}; places with bosses: {Places}",
                string.Join(" | ", candidates.Take(12).Select(c => $"{c.Entity.CanonicalName} ({c.Entity.Type}, {c.Score:0.00}, \"{c.OcrText}\")")),
                string.Join(", ", bossesIn.Keys));
            foreach (var m in pick.MonstersSeen) fight.AddObserved(m);
            logger.LogInformation("Detection: {Entity} ({Score:0.00}) in {Ms} ms, {Count} candidates",
                pick.Entity?.Id, pick.Score, sw.ElapsedMilliseconds, candidates.Count);

            if (pick.Entity is { } e && Show is not null)
            {
                await Show(e.Id);
                status.Info(Loc.Instance.Format("AutoDetect.Found", e.CanonicalName));
                return e;
            }

            var guesses = candidates.Select(c => c.Entity.CanonicalName).Distinct().Take(3).ToList();
            status.Error(guesses.Count > 0
                ? Loc.Instance.Format("AutoDetect.Unsure", string.Join(", ", guesses))
                : Loc.Instance["AutoDetect.Nothing"]);
            return null;
        }
        catch (Exception ex) when (ex is CaptureException or Application.Abstractions.OcrException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Detection failed");
            status.Error(Loc.Instance.Format("AutoDetect.Error", ex.Message));
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
