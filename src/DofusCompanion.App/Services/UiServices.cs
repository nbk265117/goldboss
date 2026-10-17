using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Imaging;
using DofusCompanion.Application.Mechanics;
using DofusCompanion.Domain;

namespace DofusCompanion.App.Services;

/// <summary>
/// What the user has told or shown the companion about the current fight. Shared by the overlay and the
/// main window. Only confirmed recognitions are added; nothing is inferred.
/// </summary>
public sealed partial class FightContext : ObservableObject
{
    [ObservableProperty] public partial int? TurnNumber { get; set; }
    [ObservableProperty] public partial bool RosterIsComplete { get; set; }

    public ObservableCollection<GameEntity> ObservedEntities { get; } = [];

    public event EventHandler? Changed;

    public GuidanceContext ToGuidanceContext() => new()
    {
        TurnNumber = TurnNumber,
        RosterIsComplete = RosterIsComplete,
        ObservedEntityIds = ObservedEntities.Select(e => e.Id).ToHashSet(StringComparer.Ordinal),
    };

    public void AddObserved(GameEntity entity)
    {
        if (ObservedEntities.All(e => e.Id != entity.Id)) ObservedEntities.Add(entity);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        ObservedEntities.Clear();
        TurnNumber = null;
        RosterIsComplete = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnTurnNumberChanged(int? value) => Changed?.Invoke(this, EventArgs.Empty);
    partial void OnRosterIsCompleteChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Single status line shown in both the main window and the overlay. Errors are visible, not only logged.</summary>
public sealed partial class StatusService : ObservableObject
{
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial bool IsError { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public void Info(string message)
    {
        Message = message;
        IsError = false;
    }

    public void Error(string message)
    {
        Message = message;
        IsError = true;
    }
}

/// <summary>Conversions between WPF bitmaps and <see cref="RasterImage"/>, plus validated file/clipboard input.</summary>
public sealed class ImageInputService(IImageDecoder decoder)
{
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public async Task<RasterImage> LoadFileAsync(string path, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        if (!Extensions.Contains(Path.GetExtension(full).ToLowerInvariant()))
            throw new ImageRejectedException(Localization.Loc.Instance["Error.ImageFormat"]);
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException(full);
        if (info.Length > Ocr.Windows.WindowsImageDecoder.MaxFileBytes)
            throw new ImageRejectedException(Localization.Loc.Instance["Error.ImageTooLarge"]);
        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        return await decoder.DecodeAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>Reads an image from the clipboard; returns null if the clipboard has none.</summary>
    public static RasterImage? FromClipboard()
    {
        if (!Clipboard.ContainsImage()) return null;
        var src = Clipboard.GetImage();
        return src is null ? null : FromBitmapSource(src);
    }

    public static RasterImage FromBitmapSource(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var w = converted.PixelWidth;
        var h = converted.PixelHeight;
        if ((long)w * h > RasterImage.MaxPixels) throw new ImageRejectedException(Localization.Loc.Instance["Error.ImageTooLarge"]);
        var pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);
        return new RasterImage(w, h, pixels);
    }

    public static BitmapSource ToBitmapSource(RasterImage image)
    {
        var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bmp.Freeze();
        return bmp;
    }

    public static void SavePng(RasterImage image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmapSource(image)));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
