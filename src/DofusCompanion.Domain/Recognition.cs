namespace DofusCompanion.Domain;

public enum ConfirmationState
{
    /// <summary>Not yet reviewed by the user.</summary>
    Pending,
    /// <summary>Accepted automatically because the match was unambiguous (still correctable).</summary>
    AutoAccepted,
    ConfirmedByUser,
    CorrectedByUser,
    RejectedByUser,
}

public enum DetectionMethod
{
    ManualSearch,
    ImageImport,
    ClipboardImage,
    ScreenCapture,
}

/// <summary>Pixel rectangle in source-image coordinates.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public sealed record RecognitionObservation
{
    public required Guid Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required DetectionMethod Method { get; init; }
    public string? OcrText { get; init; }
    public PixelRect? Region { get; init; }
    public IReadOnlyList<string> CandidateEntityIds { get; init; } = [];
    public IReadOnlyList<double> CandidateScores { get; init; } = [];
    public ConfirmationState Confirmation { get; init; }
    public string? ConfirmedEntityId { get; init; }
    /// <summary>Only set when the user explicitly enabled screenshot retention.</summary>
    public string? ScreenshotPath { get; init; }
}
