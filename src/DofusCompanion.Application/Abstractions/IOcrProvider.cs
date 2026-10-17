using DofusCompanion.Application.Imaging;
using DofusCompanion.Domain;

namespace DofusCompanion.Application.Abstractions;

public sealed record OcrWord(string Text, PixelRect Bounds, double? Confidence);

public sealed record OcrLine(string Text, PixelRect Bounds, IReadOnlyList<OcrWord> Words);

public sealed record OcrResult(string Language, IReadOnlyList<OcrLine> Lines, TimeSpan Duration)
{
    public string FullText => string.Join(Environment.NewLine, Lines.Select(l => l.Text));
}

public sealed record OcrAvailability(bool IsAvailable, IReadOnlyList<string> Languages, string? Problem);

/// <summary>Replaceable local OCR engine. Must not send images anywhere.</summary>
public interface IOcrProvider
{
    string Name { get; }
    /// <summary>Largest width/height the engine accepts; callers downscale above it.</summary>
    int MaxImageDimension { get; }
    OcrAvailability GetAvailability();
    /// <summary>Recognizes text. Throws <see cref="OcrException"/> with an actionable message on failure.</summary>
    Task<OcrResult> RecognizeAsync(RasterImage image, string language, CancellationToken ct);
}

public sealed class OcrException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Decodes user-supplied image files/bytes into raw pixels with validation (size, format).</summary>
public interface IImageDecoder
{
    Task<RasterImage> DecodeAsync(byte[] data, CancellationToken ct);
}

public sealed class ImageRejectedException(string message, Exception? inner = null) : Exception(message, inner);
