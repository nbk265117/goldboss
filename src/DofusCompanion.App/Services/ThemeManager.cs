using System.Windows;

namespace DofusCompanion.App.Services;

/// <summary>Swaps the colour dictionary (Themes/Dark.xaml or Themes/Light.xaml) at runtime. Styles use DynamicResource.</summary>
public static class ThemeManager
{
    public static string Current { get; private set; } = "Dark";

    public static void Apply(string theme)
    {
        if (theme is not ("Dark" or "Light")) theme = "Dark";
        var app = System.Windows.Application.Current;
        if (app is null) return;
        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Themes/Colors.", StringComparison.Ordinal) == true);
        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/DofusGoldBoss;component/Themes/Colors.{theme}.xaml", UriKind.Absolute) };
        if (existing is null) dictionaries.Insert(0, next);
        else dictionaries[dictionaries.IndexOf(existing)] = next;
        Current = theme;
    }
}
