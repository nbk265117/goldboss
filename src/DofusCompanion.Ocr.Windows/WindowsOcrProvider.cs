using System.Diagnostics;
using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Imaging;
using DofusCompanion.Domain;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using AppOcrResult = DofusCompanion.Application.Abstractions.OcrResult;
using AppOcrLine = DofusCompanion.Application.Abstractions.OcrLine;
using AppOcrWord = DofusCompanion.Application.Abstractions.OcrWord;

namespace DofusCompanion.Ocr.Windows;

/// <summary>
/// OCR through the Windows built-in engine (Windows.Media.Ocr). Runs fully locally; no image leaves the PC.
/// Recognizer languages are the Windows "OCR" language features installed on the machine.
/// </summary>
public sealed class WindowsOcrProvider : IOcrProvider
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Name => "Windows.Media.Ocr";

    public int MaxImageDimension
    {
        get
        {
            try { return (int)OcrEngine.MaxImageDimension; }
            catch (Exception) { return 2600; } // documented historical limit, used if the API is unavailable
        }
    }

    public OcrAvailability GetAvailability()
    {
        try
        {
            var languages = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
            return languages.Count == 0
                ? new OcrAvailability(false, [], "No Windows OCR language is installed. Install one via Settings > Time & language > Language (\"Optical character recognition\" feature).")
                : new OcrAvailability(true, languages, null);
        }
        catch (Exception ex)
        {
            return new OcrAvailability(false, [], $"Windows OCR is not available on this system: {ex.Message}");
        }
    }

    public async Task<AppOcrResult> RecognizeAsync(RasterImage image, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width > MaxImageDimension || image.Height > MaxImageDimension)
            throw new OcrException($"Image {image.Width}x{image.Height} exceeds the OCR engine limit ({MaxImageDimension}px).");

        OcrEngine? engine;
        try
        {
            engine = OcrEngine.TryCreateFromLanguage(new Language(language));
        }
        catch (ArgumentException ex)
        {
            throw new OcrException($"Invalid OCR language tag '{language}'.", ex);
        }

        if (engine is null)
            throw new OcrException($"The OCR language '{language}' is not installed on Windows.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                CryptographicBuffer.CreateFromByteArray(image.Pixels), BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap).AsTask(timeout.Token).ConfigureAwait(false);

            var lines = result.Lines.Select(line =>
            {
                var words = line.Words.Select(w => new AppOcrWord(w.Text, ToRect(w.BoundingRect), null)).ToList();
                return new AppOcrLine(line.Text, Union(words.Select(w => w.Bounds)), words);
            }).ToList();

            return new AppOcrResult(language, lines, sw.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OcrException($"OCR timed out after {Timeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OcrException)
        {
            throw new OcrException($"Windows OCR failed: {ex.Message}", ex);
        }
    }

    private static PixelRect ToRect(global::Windows.Foundation.Rect r) =>
        new((int)Math.Floor(r.X), (int)Math.Floor(r.Y), (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height));

    private static PixelRect Union(IEnumerable<PixelRect> rects)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        foreach (var r in rects)
        {
            x0 = Math.Min(x0, r.X);
            y0 = Math.Min(y0, r.Y);
            x1 = Math.Max(x1, r.X + r.Width);
            y1 = Math.Max(y1, r.Y + r.Height);
        }

        return x0 == int.MaxValue ? default : new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }
}
