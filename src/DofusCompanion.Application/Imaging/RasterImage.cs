using DofusCompanion.Domain;

namespace DofusCompanion.Application.Imaging;

/// <summary>An uncompressed 32-bit BGRA image (premultiplied alpha ignored). Stride is always Width * 4.</summary>
public sealed class RasterImage
{
    /// <summary>Hard limit protecting memory against malformed or hostile inputs (about 160 MB of pixels).</summary>
    public const int MaxPixels = 40_000_000;

    public RasterImage(int width, int height, byte[] bgra)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((long)width * height > MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Image exceeds the maximum supported size.");
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length != width * height * 4)
            throw new ArgumentException("Pixel buffer size does not match dimensions.", nameof(bgra));

        Width = width;
        Height = height;
        Pixels = bgra;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride => Width * 4;

    public static RasterImage Blank(int width, int height, byte value = 255)
    {
        var px = new byte[width * height * 4];
        Array.Fill(px, value);
        return new RasterImage(width, height, px);
    }

    public PixelRect Bounds => new(0, 0, Width, Height);
}
