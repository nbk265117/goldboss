using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DofusCompanion.App.ViewModels;

namespace DofusCompanion.App.Themes;

/// <summary>true → Visible (Invert=true flips). Also treats non-empty strings/collections and non-null objects as true.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            ICollection c => c.Count > 0,
            int i => i != 0,
            _ => true,
        };
        return truthy ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Tone → theme brush. Parameter "bg" selects the soft background variant.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bg = parameter as string == "bg";
        var key = value is Tone t
            ? t switch
            {
                Tone.Good => bg ? "GoodBg" : "Good",
                Tone.Caution => bg ? "CautionBg" : "Caution",
                Tone.Bad => bg ? "BadBg" : "Bad",
                _ => bg ? "PanelAlt" : "Muted",
            }
            : "Muted";
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Nullable int ⇄ text box (empty = null). Invalid input keeps the previous value.</summary>
public sealed class NullableIntConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString() ?? "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? null
        : int.TryParse((string)value!, NumberStyles.Integer, culture, out var n) && n is > 0 and < 1000 ? n
        : Binding.DoNothing;
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? d.ToString("P0", culture) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
