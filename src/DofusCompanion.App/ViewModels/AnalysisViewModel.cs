using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DofusCompanion.App.Localization;
using DofusCompanion.App.Services;
using DofusCompanion.Application;
using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Imaging;
using DofusCompanion.Application.Matching;
using DofusCompanion.Application.Recognition;
using DofusCompanion.Application.Settings;
using DofusCompanion.Domain;
using DofusCompanion.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DofusCompanion.App.ViewModels;

public sealed partial class CandidateLine(RecognizedCandidate candidate) : ObservableObject
{
    public RecognizedCandidate Candidate { get; } = candidate;
    public string Name => Candidate.Entity.CanonicalName;
    public string TypeLabel => GuidancePresenter.TypeLabel(Candidate.Entity.Type);
    public string ScoreText => Candidate.Score.ToString("P0", CultureInfo.CurrentCulture);
    public string Evidence => Loc.Instance.Format("Analysis.Evidence", Candidate.OcrText, Candidate.MatchedName, Candidate.Variant);
    public bool IsExact => Candidate.Score >= 0.999;

    [ObservableProperty] public partial ConfirmationState State { get; set; } = ConfirmationState.Pending;
    public string StateLabel => Loc.Instance[$"Confirmation.{State}"];
    partial void OnStateChanged(ConfirmationState value) => OnPropertyChanged(nameof(StateLabel));
}

/// <summary>
/// Screenshot analysis: image from file / clipboard / opt-in capture → OCR variants → candidates. Nothing is
/// accepted silently when ambiguous; the user confirms, rejects or picks another entity.
/// </summary>
public sealed partial class AnalysisViewModel : ObservableObject
{
    private readonly RecognitionService _recognition;
    private readonly KnowledgeService _knowledge;
    private readonly IKnowledgeRepository _repository;
    private readonly ImageInputService _images;
    private readonly ScreenCaptureService _capture;
    private readonly ISettingsStore _settings;
    private readonly FightContext _fight;
    private readonly StatusService _status;
    private readonly AppPaths _paths;
    private readonly ILogger<AnalysisViewModel> _logger;
    private CancellationTokenSource? _cts;
    private RasterImage? _image;
    private DetectionMethod _method;

    public AnalysisViewModel(RecognitionService recognition, KnowledgeService knowledge, IKnowledgeRepository repository, ImageInputService images,
        ScreenCaptureService capture, ISettingsStore settings, FightContext fight, StatusService status, AppPaths paths, ILogger<AnalysisViewModel> logger)
    {
        _recognition = recognition;
        _knowledge = knowledge;
        _repository = repository;
        _images = images;
        _capture = capture;
        _settings = settings;
        _fight = fight;
        _status = status;
        _paths = paths;
        _logger = logger;
    }

    public ObservableCollection<CandidateLine> Candidates { get; } = [];
    public ObservableCollection<ResultLine> ManualResults { get; } = [];

    [ObservableProperty] public partial BitmapSource? Preview { get; private set; }
    [ObservableProperty] public partial string OcrText { get; private set; } = "";
    [ObservableProperty] public partial string AttemptsSummary { get; private set; } = "";
    [ObservableProperty] public partial string OutcomeText { get; private set; } = "";
    [ObservableProperty] public partial Tone OutcomeTone { get; private set; }
    [ObservableProperty] public partial bool IsRunning { get; private set; }
    [ObservableProperty] public partial string Progress { get; private set; } = "";
    [ObservableProperty] public partial bool HasImage { get; private set; }
    [ObservableProperty] public partial bool IsPaused { get; set; }
    [ObservableProperty] public partial string ManualQuery { get; set; } = "";
    [ObservableProperty] public partial CandidateLine? SelectedCandidate { get; set; }
    /// <summary>Selected region in source pixels (set by the view's drag-selection), null = whole image.</summary>
    [ObservableProperty] public partial PixelRect? Region { get; set; }
    [ObservableProperty] public partial string RegionText { get; private set; } = "";
    [ObservableProperty] public partial Guid? LastObservationId { get; private set; }

    public bool CaptureEnabled => _settings.Current.ScreenCaptureEnabled;
    public int ImageWidth => _image?.Width ?? 0;
    public int ImageHeight => _image?.Height ?? 0;

    /// <summary>Raised when the user confirms an entity; hosts show its guidance.</summary>
    public event EventHandler<GameEntity>? EntityConfirmed;

    /// <summary>Set by the host: the window whose monitor is captured.</summary>
    public Func<System.Windows.Window?>? CaptureReference { get; set; }

    /// <summary>Set by the host: hides the overlay from our own capture for its duration (null = nothing to hide).</summary>
    public Func<Task<IDisposable?>>? BeforeCapture { get; set; }

    public void RefreshCaptureAvailability() => OnPropertyChanged(nameof(CaptureEnabled));

    partial void OnRegionChanged(PixelRect? value) =>
        RegionText = value is { } r ? Loc.Instance.Format("Analysis.Region", r.X, r.Y, r.Width, r.Height) : Loc.Instance["Analysis.WholeImage"];

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        var dlg = new OpenFileDialog
        {
            Filter = $"{Loc.Instance["Analysis.ImageFilter"]}|{string.Join(';', ImageInputService.Extensions.Select(e => "*" + e))}",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        await LoadAndRunAsync(() => _images.LoadFileAsync(dlg.FileName, CancellationToken.None), DetectionMethod.ImageImport);
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        RasterImage? img;
        try { img = ImageInputService.FromClipboard(); }
        catch (Exception ex) when (ex is ImageRejectedException or System.Runtime.InteropServices.COMException)
        {
            _status.Error(ex.Message);
            return;
        }

        if (img is null)
        {
            _status.Error(Loc.Instance["Analysis.NoClipboardImage"]);
            return;
        }

        await LoadAndRunAsync(() => Task.FromResult(img), DetectionMethod.ClipboardImage);
    }

    [RelayCommand]
    public async Task CaptureAsync()
    {
        if (!_settings.Current.ScreenCaptureEnabled)
        {
            _status.Error(Loc.Instance["Analysis.CaptureDisabled"]);
            return;
        }

        if (IsPaused)
        {
            _status.Info(Loc.Instance["Analysis.CapturePaused"]);
            return;
        }

        var reference = CaptureReference?.Invoke();
        if (reference is null) return;
        await LoadAndRunAsync(async () =>
        {
            using var hidden = BeforeCapture is null ? null : await BeforeCapture();
            try { return _capture.CaptureMonitorOf(reference); }
            catch (CaptureException ex) { throw new ImageRejectedException(ex.Message); }
        }, DetectionMethod.ScreenCapture);
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    public async Task LoadAndRunAsync(Func<Task<RasterImage>> load, DetectionMethod method)
    {
        try
        {
            var img = await load();
            SetImage(img, method);
            await RunAsync();
        }
        catch (Exception ex) when (ex is ImageRejectedException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Image input rejected");
            OutcomeText = ex.Message;
            OutcomeTone = Tone.Bad;
            _status.Error(ex.Message);
        }
    }

    private void SetImage(RasterImage img, DetectionMethod method)
    {
        _image = img;
        _method = method;
        Region = null;
        Preview = ImageInputService.ToBitmapSource(img);
        HasImage = true;
        OnPropertyChanged(nameof(ImageWidth));
        OnPropertyChanged(nameof(ImageHeight));
    }

    [RelayCommand]
    public async Task RunAsync()
    {
        if (_image is null) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsRunning = true;
        _status.IsBusy = true;
        Candidates.Clear();
        OcrText = "";
        AttemptsSummary = "";
        OutcomeText = Loc.Instance["Analysis.Running"];
        OutcomeTone = Tone.Neutral;
        try
        {
            var matcher = await _knowledge.GetMatcherAsync(cts.Token);
            var request = new RecognitionRequest
            {
                Image = _image,
                Method = _method,
                Region = Region,
                Language = _settings.Current.OcrLanguage,
                Variants = _settings.Current.OcrUseVariants ? null : [PreprocessVariant.Defaults[0]],
            };
            var progress = new Progress<string>(v => Progress = Loc.Instance.Format("Analysis.Variant", v));
            var report = await _recognition.RecognizeAsync(request, matcher, progress, cts.Token);
            ShowReport(report);
            await SaveObservationAsync(report);
        }
        catch (OperationCanceledException)
        {
            OutcomeText = Loc.Instance["Analysis.Cancelled"];
            OutcomeTone = Tone.Neutral;
        }
        finally
        {
            IsRunning = false;
            _status.IsBusy = false;
            Progress = "";
        }
    }

    private void ShowReport(RecognitionReport report)
    {
        LastObservationId = report.ObservationId;
        OcrText = report.AllOcrText;
        AttemptsSummary = string.Join(Environment.NewLine, report.Attempts.Select(a =>
            a.Result is null
                ? Loc.Instance.Format("Analysis.AttemptFailed", a.Variant, a.Error)
                : Loc.Instance.Format("Analysis.Attempt", a.Variant, a.Result.Lines.Count, (int)a.Duration.TotalMilliseconds)));

        foreach (var c in report.Candidates)
        {
            var line = new CandidateLine(c);
            // Only an unambiguous, high-scoring top candidate is pre-accepted; it stays correctable.
            if (report.Outcome == MatchOutcome.Confident && c == report.Candidates[0]) line.State = ConfirmationState.AutoAccepted;
            Candidates.Add(line);
        }

        if (report.FatalError is not null)
        {
            OutcomeText = Loc.Instance.Format("Analysis.Fatal", report.FatalError);
            OutcomeTone = Tone.Bad;
            _status.Error(OutcomeText);
            return;
        }

        (OutcomeText, OutcomeTone) = report.Outcome switch
        {
            MatchOutcome.Confident => (Loc.Instance.Format("Analysis.Confident", report.Candidates[0].Entity.CanonicalName), Tone.Good),
            MatchOutcome.NeedsConfirmation => (Loc.Instance["Analysis.NeedsConfirmation"], Tone.Caution),
            MatchOutcome.Ambiguous => (Loc.Instance["Analysis.Ambiguous"], Tone.Caution),
            _ => (Loc.Instance["Analysis.NoMatch"], Tone.Bad),
        };
        _status.Info(OutcomeText);

        if (report.Outcome == MatchOutcome.Confident)
        {
            _fight.AddObserved(report.Candidates[0].Entity);
            EntityConfirmed?.Invoke(this, report.Candidates[0].Entity);
        }
    }

    [RelayCommand]
    private async Task ConfirmAsync(CandidateLine? line)
    {
        if (line is null) return;
        line.State = ConfirmationState.ConfirmedByUser;
        _fight.AddObserved(line.Candidate.Entity);
        EntityConfirmed?.Invoke(this, line.Candidate.Entity);
        await LearnAliasAsync(line.Candidate.Entity, line.Candidate.OcrText, line.Candidate.Score);
        await UpdateObservationAsync(ConfirmationState.ConfirmedByUser, line.Candidate.Entity.Id);
    }

    [RelayCommand]
    private async Task RejectAsync(CandidateLine? line)
    {
        if (line is null) return;
        line.State = ConfirmationState.RejectedByUser;
        await UpdateObservationAsync(ConfirmationState.RejectedByUser, null);
    }

    partial void OnManualQueryChanged(string value) => _ = SearchManualAsync(value);

    private async Task SearchManualAsync(string q)
    {
        ManualResults.Clear();
        if (q.Length < 2) return;
        foreach (var r in await _knowledge.SearchAsync(q, new SearchFilter { MaxResults = 10, IncludeMechanicText = false }))
            ManualResults.Add(new ResultLine(r));
    }

    /// <summary>The user picks the right entity manually (correction of a wrong or missing recognition).</summary>
    [RelayCommand]
    private async Task ChooseManualAsync(ResultLine? r)
    {
        if (r is null) return;
        _fight.AddObserved(r.Result.Entity);
        EntityConfirmed?.Invoke(this, r.Result.Entity);
        ManualQuery = "";
        if (SelectedCandidate is { } wrong && wrong.Candidate.Entity.Id != r.Id)
        {
            wrong.State = ConfirmationState.CorrectedByUser;
            await LearnAliasAsync(r.Result.Entity, wrong.Candidate.OcrText, 0);
        }

        await UpdateObservationAsync(ConfirmationState.CorrectedByUser, r.Id);
    }

    [RelayCommand]
    private void Clear()
    {
        _cts?.Cancel();
        _image = null;
        Preview = null;
        HasImage = false;
        Region = null;
        Candidates.Clear();
        OcrText = "";
        AttemptsSummary = "";
        OutcomeText = "";
        LastObservationId = null;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void ClearRegion() => Region = null;

    /// <summary>Saves the selected zone of a screen capture as the zone automatic detection reads.</summary>
    [RelayCommand]
    private void UseRegionForAutoDetect()
    {
        if (Region is not { } r || _method != DetectionMethod.ScreenCapture)
        {
            _status.Error(Loc.Instance["AutoDetect.ZoneNeedsCapture"]);
            return;
        }

        _settings.Current.AutoDetectRegion = new CaptureRegion { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };
        _settings.Save();
        _status.Info(Loc.Instance.Format("AutoDetect.ZoneSaved", r.Width, r.Height));
    }

    /// <summary>
    /// With explicit consent (Settings › learn from corrections), a confirmed OCR reading that differs from
    /// every known name becomes a local alias, improving later matches. Never done silently.
    /// </summary>
    private async Task LearnAliasAsync(GameEntity entity, string ocrText, double score)
    {
        if (!_settings.Current.LearnFromCorrections || score >= 0.999) return;
        var norm = TextNormalizer.Normalize(ocrText);
        if (norm.Length < 3 || norm.Length > 60) return;
        if (entity.AllNames().Any(n => TextNormalizer.Normalize(n) == norm)) return;
        await _repository.AddAliasAsync(entity.Id, new EntityAlias(ocrText.Trim(), null, RecordOrigin.User));
        _knowledge.Invalidate();
        _status.Info(Loc.Instance.Format("Analysis.AliasLearned", ocrText.Trim(), entity.CanonicalName));
    }

    private async Task SaveObservationAsync(RecognitionReport report)
    {
        if (!_settings.Current.SaveObservations || _image is null) return;
        string? shot = null;
        if (_settings.Current.KeepScreenshots)
        {
            Directory.CreateDirectory(_paths.Screenshots);
            shot = Path.Combine(_paths.Screenshots, $"{report.Timestamp:yyyyMMdd-HHmmss}-{report.ObservationId:N}.png");
            ImageInputService.SavePng(_image, shot);
            PruneScreenshots();
        }

        await _repository.RecordObservationAsync(ToObservation(report, ConfirmationState.Pending, null, shot));
        _lastReport = report;
        _lastShot = shot;
    }

    private RecognitionReport? _lastReport;
    private string? _lastShot;

    private async Task UpdateObservationAsync(ConfirmationState state, string? entityId)
    {
        if (!_settings.Current.SaveObservations || _lastReport is null) return;
        await _repository.RecordObservationAsync(ToObservation(_lastReport, state, entityId, _lastShot));
    }

    private RecognitionObservation ToObservation(RecognitionReport r, ConfirmationState state, string? entityId, string? shot) => new()
    {
        Id = r.ObservationId,
        Timestamp = r.Timestamp,
        Method = r.Method,
        OcrText = r.AllOcrText,
        Region = Region,
        CandidateEntityIds = r.Candidates.Select(c => c.Entity.Id).ToList(),
        CandidateScores = r.Candidates.Select(c => c.Score).ToList(),
        Confirmation = state,
        ConfirmedEntityId = entityId,
        ScreenshotPath = shot,
    };

    private void PruneScreenshots()
    {
        try
        {
            var limit = DateTime.Now.AddDays(-_settings.Current.ScreenshotRetentionDays);
            foreach (var f in Directory.GetFiles(_paths.Screenshots, "*.png"))
                if (File.GetCreationTime(f) < limit) File.Delete(f);
        }
        catch (IOException ex) { _logger.LogWarning(ex, "Screenshot pruning failed"); }
    }
}
