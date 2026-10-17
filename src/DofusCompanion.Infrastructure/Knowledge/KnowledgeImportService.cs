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
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("seed/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var json = ReadResource(asm, name);
            var hash = Hash(Encoding.UTF8.GetBytes(json));
            var package = await serializer.ReadAsync(json, ct).ConfigureAwait(false);
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

        return applied;
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
