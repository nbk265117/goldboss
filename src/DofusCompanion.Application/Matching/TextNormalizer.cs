using System.Globalization;
using System.Text;

namespace DofusCompanion.Application.Matching;

/// <summary>
/// Canonical text form used for every name comparison: Unicode compatibility decomposition, accents removed,
/// ligatures expanded, lower-cased, punctuation turned into spaces, whitespace collapsed.
/// "Kralamoure  Géant !" and "kralamoure geant" both normalize to "kralamoure geant".
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        var pendingSpace = false;

        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            string? expanded = ch switch
            {
                'œ' or 'Œ' => "oe",
                'æ' or 'Æ' => "ae",
                'ß' => "ss",
                _ => null,
            };

            if (expanded is null && !char.IsLetterOrDigit(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            if (expanded is not null)
                sb.Append(expanded);
            else
                sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    public static string[] Tokenize(string normalized) =>
        normalized.Length == 0 ? [] : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
