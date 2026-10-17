using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Imaging;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace DofusCompanion.Ocr.Windows;

/// <summary>
/// Decodes PNG/JPEG/BMP/GIF/TIFF through Windows Imaging Component. The file signature is checked before
/// decoding and dimensions are checked before pixels are allocated, so malformed or oversized inputs are
/// rejected safely.
/// </summary>
public sealed class WindowsImageDecoder : IImageDecoder
{
    public const int MaxFileBytes = 30 * 1024 * 1024;

    public static bool HasKnownSignature(ReadOnlySpan<byte> d) =>
        (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) // PNG
        || (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) // JPEG
        || (d.Length >= 2 && d[0] == 0x42 && d[1] == 0x4D) // BMP
        || (d.Length >= 4 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x38) // GIF
        || (d.Length >= 4 && ((d[0] == 0x49 && d[1] == 0x49 && d[2] == 0x2A && d[3] == 0) || (d[0] == 0x4D && d[1] == 0x4D && d[2] == 0 && d[3] == 0x2A))); // TIFF

    public async Task<RasterImage> DecodeAsync(byte[] data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) throw new ImageRejectedException("The image is empty.");
        if (data.Length > MaxFileBytes) throw new ImageRejectedException($"The image file is larger than {MaxFileBytes / (1024 * 1024)} MB.");
        if (!HasKnownSignature(data)) throw new ImageRejectedException("Unsupported image format. Use PNG, JPEG, BMP, GIF or TIFF.");

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(CryptographicBuffer.CreateFromByteArray(data)).AsTask(ct).ConfigureAwait(false);
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
            if ((long)decoder.PixelWidth * decoder.PixelHeight > RasterImage.MaxPixels)
                throw new ImageRejectedException($"The image is too large ({decoder.PixelWidth}x{decoder.PixelHeight}).");

            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct).ConfigureAwait(false);
            var buffer = new global::Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
            bitmap.CopyToBuffer(buffer);
            CryptographicBuffer.CopyToByteArray(buffer, out var pixels);
            return new RasterImage(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
        }
        catch (Exception ex) when (ex is not ImageRejectedException and not OperationCanceledException)
        {
            throw new ImageRejectedException($"The image could not be decoded: {ex.Message}", ex);
        }
    }
}
