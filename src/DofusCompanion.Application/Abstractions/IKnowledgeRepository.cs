using DofusCompanion.Application.Knowledge;
using DofusCompanion.Domain;

namespace DofusCompanion.Application.Abstractions;

public sealed record DatabaseStatus(string Path, int SchemaVersion, int EntityCount, int MechanicCount, int SourceCount, long FileSizeBytes);

/// <summary>Persistent local knowledge store. Implementations must apply writes transactionally.</summary>
public interface IKnowledgeRepository
{
    Task<IReadOnlyList<GameEntity>> GetAllEntitiesAsync(CancellationToken ct = default);
    Task<GameEntity?> GetEntityAsync(string id, CancellationToken ct = default);
    /// <summary>Relations where the entity is either end.</summary>
    Task<IReadOnlyList<EntityRelation>> GetRelationsAsync(string entityId, CancellationToken ct = default);
    Task<IReadOnlyList<EntityRelation>> GetAllRelationsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Mechanic>> GetMechanicsForEntityAsync(string entityId, CancellationToken ct = default);
    Task<IReadOnlyList<Mechanic>> GetAllMechanicsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeSource>> GetAllSourcesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GameVersion>> GetGameVersionsAsync(CancellationToken ct = default);

    /// <summary>Applies an import plan in one transaction; conflicting items are applied only per the given resolutions.</summary>
    Task ApplyImportAsync(ImportPlan plan, CancellationToken ct = default);

    /// <summary>True if a package with this id and content hash was already applied.</summary>
    Task<bool> WasImportedAsync(string packageId, string contentHash, CancellationToken ct = default);

    Task UpsertSourceAsync(KnowledgeSource source, CancellationToken ct = default);
    Task UpsertEntityAsync(GameEntity entity, IReadOnlyList<EntityRelation> relations, CancellationToken ct = default);
    Task UpsertMechanicAsync(Mechanic mechanic, CancellationToken ct = default);
    Task AddAliasAsync(string entityId, EntityAlias alias, CancellationToken ct = default);

    Task RecordObservationAsync(RecognitionObservation observation, CancellationToken ct = default);
    Task<int> DeleteObservationsAsync(CancellationToken ct = default);
    /// <summary>Deletes these mechanics (with their steps, notes, sources…). Returns how many were deleted.</summary>
    Task<int> DeleteMechanicsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);

    Task<DatabaseStatus> GetStatusAsync(CancellationToken ct = default);
}
