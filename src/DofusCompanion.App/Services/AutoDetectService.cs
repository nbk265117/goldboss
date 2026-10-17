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
            var dofus = DofusForegroundWindow();
            RasterImage image;
            using (BeforeCapture is null ? null : await BeforeCapture())
            {
                image = dofus != 0 ? ScreenCaptureService.CaptureMonitorOf(dofus)
                    : CaptureFallback?.Invoke() ?? ScreenCaptureService.CapturePrimary();
            }

            if (settings.Current.AutoDetectRegion is { Width: > 8, Height: > 8 } r
                && r.X + r.Width <= image.Width && r.Y + r.Height <= image.Height)
                image = ImagePreprocessor.Crop(image, new PixelRect(r.X, r.Y, r.Width, r.Height));

            var sw = Stopwatch.StartNew();
            var report = await recognition.RecognizeAsync(new RecognitionRequest
            {
                Image = image,
                Method = DetectionMethod.ScreenCapture,
                Language = settings.Current.OcrLanguage,
            }, await knowledge.GetMatcherAsync(ct), null, ct);

            var pick = AutoDetectDecider.Pick(report.Candidates);
            foreach (var m in pick.MonstersSeen) fight.AddObserved(m);
            logger.LogInformation("Detection: {Entity} ({Score:0.00}) in {Ms} ms, {Count} candidates",
                pick.Entity?.Id, pick.Score, sw.ElapsedMilliseconds, report.Candidates.Count);

            if (pick.Entity is { } e && Show is not null)
            {
                await Show(e.Id);
                status.Info(Loc.Instance.Format("AutoDetect.Found", e.CanonicalName));
                return e;
            }

            var guesses = report.Candidates.Select(c => c.Entity.CanonicalName).Distinct().Take(3).ToList();
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

    /// <summary>
    /// The foreground window if it belongs to the Dofus client (process name starting with "Dofus", excluding
    /// this app). Only the process NAME is read; the game process is never opened or inspected.
    /// </summary>
    private static nint DofusForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0) return 0;
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return 0;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.StartsWith("Dofus", StringComparison.OrdinalIgnoreCase)
                   && !p.ProcessName.StartsWith("DofusGoldBoss", StringComparison.OrdinalIgnoreCase) ? hwnd : 0;
        }
        catch (ArgumentException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }
}
