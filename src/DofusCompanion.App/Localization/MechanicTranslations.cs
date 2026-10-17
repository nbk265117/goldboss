using System.Reflection;
using System.Text.Json;
using DofusCompanion.Domain;

namespace DofusCompanion.App.Localization;

/// <summary>
/// Translations of the bundled mechanic texts (Localization/Mechanics/mechanics.{en,ar}.json, embedded):
/// { mechanicId: { fieldKey: text } } with keys such as "title", "summary", "notes.0", "prerequisites.1",
/// "unknownAspects.0", "sequences.0.label", "sequences.0.steps.3.action" / ".rationale" / ".when".
/// Any missing key keeps the French text, and source quotes are never translated (they are verbatim citations).
/// </summary>
public static class MechanicTranslations
{
    private static readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> Tables = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    /// <summary>Translates <paramref name="m"/> into the current interface language (French: unchanged).</summary>
    public static Mechanic Apply(Mechanic m) => Apply(m, Loc.Instance.Language);

    public static Mechanic Apply(Mechanic m, string language)
    {
        if (language == Loc.DefaultLanguage || !Table(language).TryGetValue(m.Id, out var t) || t.Count == 0) return m;
        string? T(string key, string? fallback) => fallback is not null && t.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        return m with
        {
            Title = T("title", m.Title)!,
            Summary = T("summary", m.Summary),
            ExpectedStateChange = T("expectedStateChange", m.ExpectedStateChange),
            ExpectedResult = T("expectedResult", m.ExpectedResult),
            VulnerabilityDuration = T("vulnerabilityDuration", m.VulnerabilityDuration),
            TurnConditions = T("turnConditions", m.TurnConditions),
            Prerequisites = m.Prerequisites.Select((c, i) => c with { Description = T($"prerequisites.{i}", c.Description)! }).ToList(),
            Notes = m.Notes.Select((n, i) => n with { Text = T($"notes.{i}", n.Text)! }).ToList(),
            UnknownAspects = m.UnknownAspects.Select((u, i) => T($"unknownAspects.{i}", u)!).ToList(),
            Sequences = m.Sequences.Select((s, si) => s with
            {
                Label = T($"sequences.{si}.label", s.Label),
                Steps = s.Steps.Select(st =>
                {
                    var p = $"sequences.{si}.steps.{st.Order}.";
                    return st with
                    {
                        Action = T(p + "action", st.Action)!,
                        Target = T(p + "target", st.Target),
                        Position = T(p + "position", st.Position),
                        RequiredState = T(p + "requiredState", st.RequiredState),
                        Rationale = T(p + "rationale", st.Rationale),
                        When = st.When is null ? null : st.When with { Description = T(p + "when", st.When.Description)! },
                    };
                }).ToList(),
            }).ToList(),
        };
    }

    /// <summary>Number of translated mechanics for a language (diagnostics/tests).</summary>
    public static int Count(string language) => Table(language).Count;

    private static Dictionary<string, Dictionary<string, string>> Table(string language)
    {
        lock (Gate)
        {
            if (Tables.TryGetValue(language, out var cached)) return cached;
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".mechanics.{language}.json", StringComparison.Ordinal));
            var table = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            if (name is not null)
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                table = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream) ?? table;
            }

            Tables[language] = table;
            return table;
        }
    }
}
