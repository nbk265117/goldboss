using System.Text.Json;
using DofusCompanion.Application.Matching;
using DofusCompanion.Infrastructure.Knowledge;

namespace DofusCompanion.DofusDbImporter;

/// <summary>Normalized names (and aliases) of the bosses bundled with the app, to download only their pictures.</summary>
internal static class KnownNames
{
    public static HashSet<string> Bosses()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var asm = typeof(KnowledgePackageSerializer).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("seed/", StringComparison.Ordinal)))
        {
            using var doc = JsonDocument.Parse(KnowledgePackageSerializer.ReadEmbedded(res));
            foreach (var e in doc.RootElement.GetProperty("entities").EnumerateArray())
            {
                if (e.GetProperty("type").GetString() != "Boss") continue;
                var canonical = e.GetProperty("canonicalName").GetString() ?? "";
                names.Add(TextNormalizer.Normalize(canonical));
                // "Guerre (Tempête de l'Eliocalypse)" is the game's "Guerre".
                var paren = canonical.IndexOf(" (", StringComparison.Ordinal);
                if (paren > 0) names.Add(TextNormalizer.Normalize(canonical[..paren]));
                if (e.TryGetProperty("aliases", out var aliases))
                    foreach (var a in aliases.EnumerateArray())
                        names.Add(TextNormalizer.Normalize(a.GetProperty("text").GetString()));
            }
        }

        names.Remove("");
        return names;
    }
}
