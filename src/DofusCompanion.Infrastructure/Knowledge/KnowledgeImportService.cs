using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DofusCompanion.Application;
using DofusCompanion.Application.Abstractions;
using DofusCompanion.Application.Knowledge;
using DofusCompanion.Domain;
using Microsoft.Extensions.Logging;

namespace DofusCompanion.Infrastructure.Knowledge;

/// <summary>
/// Import/export workflow: read + validate → plan (preview) → apply in one transaction. Also installs the
/// bundled, verified seed packages on first run and when a newer seed ships.
/// </summary>
public sealed class KnowledgeImportService(
    IKnowledgeRepository repository,
    KnowledgeService knowledge,
    KnowledgePackageSerializer serializer,
    ILogger<KnowledgeImportService> logger)
{
    public async Task<ImportPlan> PreviewFileAsync(string path, CancellationToken ct = default)
    {
        var package = await serializer.ReadFileAsync(path, ct).ConfigureAwait(false);
        var hash = Hash(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
        return ImportPlanner.Plan(package, await knowledge.GetExistingKnowledgeAsync(ct).ConfigureAwait(false)) with { ContentHash = hash };
    }

    public async Task ApplyAsync(ImportPlan plan, CancellationToken ct = default)
    {
        await repository.ApplyImportAsync(plan, ct).ConfigureAwait(false);
        knowledge.Invalidate();
    }

    /// <summary>
    /// Applies every bundled seed package whose content changed since it was last applied. Records edited by
    /// the user are never overwritten; records that came from an earlier seed are updated to the new seed.
    /// </summary>
    public async Task<int> EnsureSeedAsync(CancellationToken ct = default)
    {
        var asm = typeof(KnowledgePackageSerializer).Assembly;
        var applied = 0;
        var seedMechanics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("seed/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var json = ReadResource(asm, name);
            var hash = Hash(Encoding.UTF8.GetBytes(json));
            var package = await serializer.ReadAsync(json, ct).ConfigureAwait(false);
            seedMechanics.UnionWith(package.Mechanics.Select(m => m.Id));
            if (await repository.WasImportedAsync(package.PackageId, hash, ct).ConfigureAwait(false))
                continue;

            var plan = ImportPlanner.Plan(package, await knowledge.GetExistingKnowledgeAsync(ct).ConfigureAwait(false), RecordOrigin.Seed)
                with { ContentHash = hash };
            foreach (var change in plan.Changes.Where(c => c.Change == ChangeKind.Conflict))
            {
                if (IsFromSeed(change.Existing) && change.AllowedResolutions.Contains(Resolution.TakeIncoming))
                    change.Resolution = Resolution.TakeIncoming;
            }

            await ApplyAsync(plan, ct).ConfigureAwait(false);
            logger.LogInformation("Seed package {Package} applied ({Hash})", package.PackageId, hash[..12]);
            applied++;
        }

        if (applied > 0)
        {
            // A mechanic merged into another or renamed in a newer seed would otherwise stay as an outdated duplicate.
            // Only seed records are removed; mechanics the user created or edited are kept.
            var stale = (await repository.GetAllMechanicsAsync(ct).ConfigureAwait(false))
                .Where(m => m.Origin == RecordOrigin.Seed && !seedMechanics.Contains(m.Id)).Select(m => m.Id).ToList();
            if (stale.Count > 0)
            {
                await repository.DeleteMechanicsAsync(stale, ct).ConfigureAwait(false);
                knowledge.Invalidate();
                logger.LogInformation("{Count} outdated seed mechanic(s) removed: {Ids}", stale.Count, string.Join(", ", stale));
            }
        }

        return applied;
    }

    /// <summary>
    /// Applies every package found in <paramref name="folder"/> (e.g. written by the DofusDB importer) with the
    /// planner's safe defaults — verified and user-edited records kept, duplicates merged — then moves it to
    /// "applied" (or "rejected" if invalid). Returns a short summary per package.
    /// </summary>
    public async Task<IReadOnlyList<string>> ApplyDroppedPackagesAsync(string folder, CancellationToken ct = default)
    {
        var results = new List<string>();
        if (!Directory.Exists(folder)) return results;
        foreach (var file in Directory.GetFiles(folder, "*" + KnowledgePackageSerializer.FileExtension).Order(StringComparer.Ordinal))
        {
            string target;
            try
            {
                var plan = await PreviewFileAsync(file, ct).ConfigureAwait(false);
                await ApplyAsync(plan, ct).ConfigureAwait(false);
                results.Add($"{plan.Package.Name}: +{plan.Count(ChangeKind.Add)}, ~{plan.Count(ChangeKind.Update)}, ={plan.Count(ChangeKind.Conflict)}");
                target = Path.Combine(folder, "applied");
            }
            catch (Exception ex) when (ex is PackageValidationException or InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                logger.LogWarning(ex, "Dropped package {File} rejected", file);
                results.Add($"{Path.GetFileName(file)}: {ex.Message.Split('\n')[0]}");
                target = Path.Combine(folder, "rejected");
            }

            Directory.CreateDirectory(target);
            File.Move(file, Path.Combine(target, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(file)}"), overwrite: true);
        }

        return results;
    }

    /// <summary>
    /// Applies the DofusDB data shipped with the release (data\dofusdb beside the exe): its pictures are copied to
    /// &lt;data&gt;\images\dofusdb (missing or changed files only) and its package is imported once, with the
    /// planner's safe defaults. Returns the number of pictures copied and of packages applied.
    /// </summary>
    public async Task<(int Images, int Packages)> ApplyBundledDofusDbAsync(string bundleDir, string imagesDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(bundleDir)) return (0, 0);
        var images = 0;
        var source = Path.Combine(bundleDir, "images");
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(imagesDir);
            foreach (var file in Directory.GetFiles(source, "*.png"))
            {
                var target = Path.Combine(imagesDir, Path.GetFileName(file));
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(file).Length) continue;
                File.Copy(file, target, overwrite: true);
                images++;
            }
        }

        var packages = 0;
        foreach (var file in Directory.GetFiles(bundleDir, "*" + KnowledgePackageSerializer.FileExtension).Order(StringComparer.Ordinal))
        {
            var hash = Hash(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false));
            var package = await serializer.ReadFileAsync(file, ct).ConfigureAwait(false);
            if (await repository.WasImportedAsync(package.PackageId, hash, ct).ConfigureAwait(false)) continue;
            await ApplyAsync(await PreviewFileAsync(file, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            logger.LogInformation("Bundled package {Package} applied ({Hash})", package.PackageId, hash[..12]);
            packages++;
        }

        return (images, packages);
    }

    /// <summary>Exports the given entities (or everything) with their mechanics, the sources they cite and their relation targets.</summary>
    public async Task<KnowledgePackage> BuildExportAsync(IReadOnlyCollection<string>? entityIds, string name, CancellationToken ct = default)
    {
        var existing = await knowledge.GetExistingKnowledgeAsync(ct).ConfigureAwait(false);
        var byId = existing.Entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var selected = entityIds is null
            ? byId.Keys.ToHashSet(StringComparer.Ordinal)
            : entityIds.Where(byId.ContainsKey).ToHashSet(StringComparer.Ordinal);

        // Include relation targets so the exported package re-imports without dropped relations.
        foreach (var r in existing.Relations.Where(r => selected.Contains(r.FromId)).ToList())
            selected.Add(r.ToId);

        var mechanics = existing.Mechanics.Where(m => selected.Contains(m.EntityId)).ToList();
        var sourceIds = mechanics.SelectMany(m => m.Sources.Select(s => s.SourceId))
            .Concat(selected.SelectMany(id => byId[id].SourceIds))
            .ToHashSet(StringComparer.Ordinal);

        return new KnowledgePackage
        {
            PackageId = "export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture),
            Name = name,
            Author = null,
            CreatedOn = DateOnly.FromDateTime(DateTime.Today),
            GameVersions = existing.GameVersions,
            Sources = existing.Sources.Where(s => sourceIds.Contains(s.Id)).ToList(),
            Entities = selected.Select(id => new PackageEntity(byId[id],
                existing.Relations.Where(r => r.FromId == id && selected.Contains(r.ToId)).ToList())).ToList(),
            Mechanics = mechanics,
        };
    }

    public async Task ExportToFileAsync(IReadOnlyCollection<string>? entityIds, string path, CancellationToken ct = default)
    {
        var package = await BuildExportAsync(entityIds, Path.GetFileNameWithoutExtension(path), ct).ConfigureAwait(false);
        await serializer.WriteFileAsync(package, path, ct).ConfigureAwait(false);
    }

    private static bool IsFromSeed(object? existing) => existing switch
    {
        PackageEntity pe => pe.Entity.Origin == RecordOrigin.Seed,
        Mechanic m => m.Origin == RecordOrigin.Seed,
        // Sources and versions have no user-edit path other than the knowledge editor, which marks them as user sources.
        KnowledgeSource s => s.Category != SourceCategory.User,
        GameVersion => true,
        _ => false,
    };

    private static string ReadResource(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
