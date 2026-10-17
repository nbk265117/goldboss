using DofusCompanion.Domain;

namespace DofusCompanion.Application.Knowledge;

public sealed record PackageEntity(GameEntity Entity, IReadOnlyList<EntityRelation> Relations);

/// <summary>In-memory form of a validated knowledge package (see data/schemas/knowledge-package.schema.json).</summary>
public sealed record KnowledgePackage
{
    public required string PackageId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public required DateOnly CreatedOn { get; init; }
    public bool TestOnly { get; init; }
    public IReadOnlyList<GameVersion> GameVersions { get; init; } = [];
    public IReadOnlyList<KnowledgeSource> Sources { get; init; } = [];
    public IReadOnlyList<PackageEntity> Entities { get; init; } = [];
    public IReadOnlyList<Mechanic> Mechanics { get; init; } = [];
}
