using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace DofusCompanion.App.Localization;

/// <summary>
/// UI string table loaded from embedded JSON files (Localization/fr.json, en.json, ar.json — Moroccan Darija). Changing
/// <see cref="Language"/> raises PropertyChanged("Item[]") so every bound string updates live.
/// Canonical entity names are never translated here: they come from the knowledge base.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string DefaultLanguage = "fr";
    public static IReadOnlyList<string> Supported { get; } = ["fr", "en", "ar"];
    public static Loc Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.Ordinal);
    private Dictionary<string, string> _current;
    private Dictionary<string, string> _fallback;

    private Loc()
    {
        foreach (var lang in Supported)
            _tables[lang] = LoadTable(lang);
        _fallback = _tables[DefaultLanguage];
        _current = _fallback;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string Language { get; private set; } = DefaultLanguage;

    public string this[string key] =>
        _current.TryGetValue(key, out var v) ? v : _fallback.TryGetValue(key, out var f) ? f : $"[{key}]";

    public string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, this[key], args);

    public void SetLanguage(string language)
    {
        if (!_tables.TryGetValue(language, out var table)) language = DefaultLanguage;
        Language = language;
        _current = _tables[language];
        var culture = CultureInfo.GetCultureInfo(language switch { "fr" => "fr-FR", "ar" => "ar-MA", _ => "en-US" });
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyCollection<string> Keys(string language) => _tables[language].Keys;

    private static Dictionary<string, string> LoadTable(string lang)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".Localization.{lang}.json", StringComparison.Ordinal));
        if (name is null) return new(StringComparer.Ordinal);
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new(StringComparer.Ordinal);
    }
}

/// <summary>XAML: Text="{l:T Overlay.Search}" — a live-updating binding to the string table.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
