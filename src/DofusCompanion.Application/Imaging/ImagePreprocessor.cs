using DofusCompanion.Domain;

namespace DofusCompanion.Application.Imaging;

public enum PreprocessOp
{
    Grayscale,
    ContrastStretch,
    Invert,
    Threshold,
    Sharpen,
}

/// <summary>
/// One OCR attempt: an optional upscale followed by pixel operations. Variants are tried in order until
/// one yields a confident entity match (see RecognitionService).
/// </summary>
public sealed record PreprocessVariant(string Name, double Scale, IReadOnlyList<PreprocessOp> Ops)
{
    public static IReadOnlyList<PreprocessVariant> Defaults { get; } =
    [
        new("original", 1.0, []),
        // Game UI text is small; upscaling plus contrast is the cheapest effective improvement.
        new("upscale-contrast", 2.0, [PreprocessOp.Grayscale, PreprocessOp.ContrastStretch]),
        // Light text on dark panels: invert and binarize so the engine sees dark-on-light text.
        new("upscale-inverted-threshold", 2.0, [PreprocessOp.Grayscale, PreprocessOp.ContrastStretch, PreprocessOp.Invert, PreprocessOp.Threshold]),
        new("upscale-sharpen", 2.0, [PreprocessOp.Grayscale, PreprocessOp.Sharpen, PreprocessOp.ContrastStretch]),
    ];
}

/// <summary>Pure, allocation-explicit image operations on <see cref="RasterImage"/>. No native dependencies.</summary>
public static class ImagePreprocessor
{
    public static RasterImage Apply(RasterImage source, PreprocessVariant variant, int maxDimension)
    {
        var scale = variant.Scale;
        var longest = Math.Max(source.Width, source.Height);
        if (longest * scale > maxDimension)
            scale = (double)maxDimension / longest;
        if ((long)(source.Width * scale) * (long)(source.Height * scale) > RasterImage.MaxPixels)
            scale = Math.Sqrt((double)RasterImage.MaxPixels / ((long)source.Width * source.Height));

        var img = Math.Abs(scale - 1.0) < 0.001 ? Clone(source) : Resize(source, scale);
        foreach (var op in variant.Ops)
        {
            switch (op)
            {
                case PreprocessOp.Grayscale: Grayscale(img); break;
                case PreprocessOp.ContrastStretch: ContrastStretch(img); break;
                case PreprocessOp.Invert: Invert(img); break;
                case PreprocessOp.Threshold: OtsuThreshold(img); break;
                case PreprocessOp.Sharpen: img = Sharpen(img); break;
            }
        }

        return img;
    }

    public static RasterImage Clone(RasterImage src) => new(src.Width, src.Height, (byte[])src.Pixels.Clone());

    public static RasterImage Crop(RasterImage src, PixelRect rect)
    {
        var x = Math.Clamp(rect.X, 0, src.Width - 1);
        var y = Math.Clamp(rect.Y, 0, src.Height - 1);
        var w = Math.Clamp(rect.Width, 1, src.Width - x);
        var h = Math.Clamp(rect.Height, 1, src.Height - y);
        var dst = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(src.Pixels, ((y + row) * src.Width + x) * 4, dst, row * w * 4, w * 4);
        return new RasterImage(w, h, dst);
    }

    /// <summary>
    /// Adds a uniform border filled with the average colour of the image's edge pixels. OCR engines often
    /// miss text that touches the image border, which is exactly what a tight user crop produces.
    /// </summary>
    public static RasterImage Pad(RasterImage src, int margin)
    {
        if (margin <= 0) return src;
        long b = 0, g = 0, r = 0, n = 0;
        void Sample(int x, int y)
        {
            var i = (y * src.Width + x) * 4;
            b += src.Pixels[i]; g += src.Pixels[i + 1]; r += src.Pixels[i + 2]; n++;
        }

        for (var x = 0; x < src.Width; x++) { Sample(x, 0); Sample(x, src.Height - 1); }
        for (var y = 0; y < src.Height; y++) { Sample(0, y); Sample(src.Width - 1, y); }

        var w = src.Width + margin * 2;
        var h = src.Height + margin * 2;
        if ((long)w * h > RasterImage.MaxPixels) return src;
        var dst = new byte[w * h * 4];
        byte fb = (byte)(b / n), fg = (byte)(g / n), fr = (byte)(r / n);
        for (var i = 0; i < dst.Length; i += 4)
        {
            dst[i] = fb; dst[i + 1] = fg; dst[i + 2] = fr; dst[i + 3] = 255;
        }

        for (var row = 0; row < src.Height; row++)
            Buffer.BlockCopy(src.Pixels, row * src.Width * 4, dst, ((row + margin) * w + margin) * 4, src.Width * 4);
        return new RasterImage(w, h, dst);
    }

    /// <summary>Bilinear resize.</summary>
    public static RasterImage Resize(RasterImage src, double scale)
    {
        var w = Math.Max(1, (int)Math.Round(src.Width * scale));
        var h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new byte[w * h * 4];
        var sp = src.Pixels;
        var xRatio = (double)src.Width / w;
        var yRatio = (double)src.Height / h;

        for (var y = 0; y < h; y++)
        {
            var sy = Math.Max(0, (y + 0.5) * yRatio - 0.5);
            var y0 = Math.Min((int)sy, src.Height - 1);
            var y1 = Math.Min(y0 + 1, src.Height - 1);
            var fy = sy - y0;
            for (var x = 0; x < w; x++)
            {
                var sx = Math.Max(0, (x + 0.5) * xRatio - 0.5);
                var x0 = Math.Min((int)sx, src.Width - 1);
                var x1 = Math.Min(x0 + 1, src.Width - 1);
                var fx = sx - x0;
                var di = (y * w + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    double top = sp[(y0 * src.Width + x0) * 4 + c] * (1 - fx) + sp[(y0 * src.Width + x1) * 4 + c] * fx;
                    double bottom = sp[(y1 * src.Width + x0) * 4 + c] * (1 - fx) + sp[(y1 * src.Width + x1) * 4 + c] * fx;
                    dst[di + c] = (byte)Math.Clamp(Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
                }
            }
        }

        return new RasterImage(w, h, dst);
    }

    public static byte Luma(byte b, byte g, byte r) => (byte)((r * 299 + g * 587 + b * 114) / 1000);

    public static void Grayscale(RasterImage img)
    {
        var p = img.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            var l = Luma(p[i], p[i + 1], p[i + 2]);
            p[i] = p[i + 1] = p[i + 2] = l;
        }
    }

    /// <summary>Stretches luminance so the 1st..99th percentiles span 0..255.</summary>
    public static void ContrastStretch(RasterImage img)
    {
        var p = img.Pixels;
        var hist = Histogram(img);
        var total = p.Length / 4;
        int lo = Percentile(hist, total, 0.01), hi = Percentile(hist, total, 0.99);
        if (hi - lo < 10) return; // flat image: stretching would only amplify noise

        var lut = new byte[256];
        for (var v = 0; v < 256; v++)
            lut[v] = (byte)Math.Clamp((v - lo) * 255 / (hi - lo), 0, 255);
        for (var i = 0; i < p.Length; i += 4)
        {
            p[i] = lut[p[i]];
            p[i + 1] = lut[p[i + 1]];
            p[i + 2] = lut[p[i + 2]];
        }
    }

    public static void Invert(RasterImage img)
    {
        var p = img.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            p[i] = (byte)(255 - p[i]);
            p[i + 1] = (byte)(255 - p[i + 1]);
            p[i + 2] = (byte)(255 - p[i + 2]);
        }
    }

    /// <summary>Global Otsu binarization on luminance.</summary>
    public static void OtsuThreshold(RasterImage img)
    {
        var hist = Histogram(img);
        var total = img.Pixels.Length / 4;
        var threshold = OtsuLevel(hist, total);
        var p = img.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            var v = Luma(p[i], p[i + 1], p[i + 2]) > threshold ? (byte)255 : (byte)0;
            p[i] = p[i + 1] = p[i + 2] = v;
        }
    }

    public static int OtsuLevel(int[] hist, int total)
    {
        double sum = 0;
        for (var t = 0; t < 256; t++) sum += t * (double)hist[t];
        double sumB = 0, best = -1;
        int wB = 0, level = 127;
        for (var t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            var wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)hist[t];
            var mB = sumB / wB;
            var mF = (sum - sumB) / wF;
            var between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best)
            {
                best = between;
                level = t;
            }
        }

        return level;
    }

    /// <summary>3x3 sharpen kernel (0,-1,0 / -1,5,-1 / 0,-1,0).</summary>
    public static RasterImage Sharpen(RasterImage src)
    {
        var w = src.Width;
        var h = src.Height;
        var s = src.Pixels;
        var d = (byte[])s.Clone();
        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var i = (y * w + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var v = 5 * s[i + c] - s[i + c - 4] - s[i + c + 4] - s[i + c - w * 4] - s[i + c + w * 4];
                    d[i + c] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        }

        return new RasterImage(w, h, d);
    }

    public static int[] Histogram(RasterImage img)
    {
        var hist = new int[256];
        var p = img.Pixels;
        for (var i = 0; i < p.Length; i += 4)
            hist[Luma(p[i], p[i + 1], p[i + 2])]++;
        return hist;
    }

    private static int Percentile(int[] hist, int total, double fraction)
    {
        var target = total * fraction;
        long acc = 0;
        for (var v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (acc >= target) return v;
        }

        return 255;
    }
}
