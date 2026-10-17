using System.Text.Json.Serialization;

namespace DofusCompanion.Infrastructure.Knowledge;

// Wire format of data/schemas/knowledge-package.schema.json. Kept separate from the domain model so the
// file format can evolve (formatVersion) without touching domain records.
#pragma warning disable CA1819, CA2227

internal sealed class PackageDto
{
    [JsonPropertyName("$schema")] public string? Schema { get; set; }
    public int FormatVersion { get; set; }
    public string PackageId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string CreatedOn { get; set; } = "";
    public bool? TestOnly { get; set; }
    public List<GameVersionDto>? GameVersions { get; set; }
    public List<SourceDto> Sources { get; set; } = [];
    public List<EntityDto> Entities { get; set; } = [];
    public List<MechanicDto> Mechanics { get; set; } = [];
}

internal sealed class GameVersionDto
{
    public string Id { get; set; } = "";
    public string? Version { get; set; }
    public string? ReleaseDate { get; set; }
    public string? SourceId { get; set; }
    public string? VerifiedOn { get; set; }
    public string? Notes { get; set; }
}

internal sealed class SourceDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Url { get; set; }
    public string? Publisher { get; set; }
    public string? Category { get; set; }
    public string Reliability { get; set; } = "Unknown";
    public string? ContentUpdatedOn { get; set; }
    public string? RetrievedOn { get; set; }
    public string? LastCheckedOn { get; set; }
    public string? Notes { get; set; }
}

internal sealed class AliasDto
{
    public string Text { get; set; } = "";
    public string? Locale { get; set; }
}

internal sealed class RelationDto
{
    public string Type { get; set; } = "";
    public string To { get; set; } = "";
}

internal sealed class EntityDto
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string CanonicalName { get; set; } = "";
    public string? Locale { get; set; }
    public string? Description { get; set; }
    public List<AliasDto>? Aliases { get; set; }
    public string? LevelText { get; set; }
    public Dictionary<string, string>? Attributes { get; set; }
    public string Verification { get; set; } = "UNKNOWN";
    public string? VersionScope { get; set; }
    public List<string>? SourceIds { get; set; }
    public List<RelationDto>? Relations { get; set; }
}

internal sealed class ConditionDto
{
    public string? Kind { get; set; }
    public string Description { get; set; } = "";
    public string? EntityId { get; set; }
    public string? State { get; set; }
    public int? Number { get; set; }
}

internal sealed class StepDto
{
    public int Order { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string? Position { get; set; }
    public string? RequiredState { get; set; }
    public ConditionDto? When { get; set; }
    public string? Rationale { get; set; }
    public string? Verification { get; set; }
}

internal sealed class SequenceDto
{
    public string? Label { get; set; }
    public List<StepDto> Steps { get; set; } = [];
}

internal sealed class NoteDto
{
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
}

internal sealed class SourceRefDto
{
    public string SourceId { get; set; } = "";
    public string? Quote { get; set; }
    public string? Locator { get; set; }
}

internal sealed class MechanicDto
{
    public string Id { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Type { get; set; } = "Other";
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    public string Verification { get; set; } = "UNKNOWN";
    public List<ConditionDto>? Prerequisites { get; set; }
    public List<SequenceDto>? Sequences { get; set; }
    public string? ExpectedStateChange { get; set; }
    public string? ExpectedResult { get; set; }
    public string? VulnerabilityDuration { get; set; }
    public string? TurnConditions { get; set; }
    public List<NoteDto>? Notes { get; set; }
    public List<string>? UnknownAspects { get; set; }
    public string? VersionScope { get; set; }
    public string? LastVerifiedOn { get; set; }
    public List<SourceRefDto> Sources { get; set; } = [];
}
