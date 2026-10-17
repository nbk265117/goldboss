using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DofusCompanion.Application.Knowledge;
using DofusCompanion.Domain;
using NJsonSchema;

namespace DofusCompanion.Infrastructure.Knowledge;

public sealed class PackageValidationException(IReadOnlyList<string> errors)
    : Exception("The knowledge package is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Take(20)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Reads and writes knowledge packages. Reading always validates against the bundled JSON Schema and then
/// runs semantic checks (duplicate ids, dangling references) before anything reaches the database.
/// Package content is treated strictly as data.
/// </summary>
public sealed class KnowledgePackageSerializer
{
    public const long MaxPackageBytes = 50L * 1024 * 1024;
    public const string FileExtension = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // Keep accented French text readable in exported files.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 32,
    };

    private readonly Lazy<Task<JsonSchema>> _schema = new(LoadSchemaAsync);

    public static string ReadEmbedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Task<JsonSchema> LoadSchemaAsync() => JsonSchema.FromJsonAsync(ReadEmbedded("knowledge-package.schema.json"));

    public async Task<KnowledgePackage> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(full), FileExtension, StringComparison.OrdinalIgnoreCase))
            throw new PackageValidationException([$"Only {FileExtension} knowledge packages can be imported."]);
        var info = new FileInfo(full);
        if (!info.Exists)
            throw new FileNotFoundException("Knowledge package not found.", full);
        if (info.Length > MaxPackageBytes)
            throw new PackageValidationException([$"File is larger than {MaxPackageBytes / (1024 * 1024)} MB."]);

        var json = await File.ReadAllTextAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);
        return await ReadAsync(json, ct).ConfigureAwait(false);
    }

    public async Task<KnowledgePackage> ReadAsync(string json, CancellationToken ct = default)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxPackageBytes)
            throw new PackageValidationException(["Package is too large."]);

        var schema = await _schema.Value.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        ICollection<NJsonSchema.Validation.ValidationError> schemaErrors;
        try
        {
            schemaErrors = schema.Validate(json);
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or JsonException)
        {
            throw new PackageValidationException([$"Not valid JSON: {ex.Message}"]);
        }

        if (schemaErrors.Count > 0)
            throw new PackageValidationException(schemaErrors.Select(e => $"{e.Path}: {e.Kind}").Distinct().ToList());

        PackageDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<PackageDto>(json, JsonOptions) ?? throw new PackageValidationException(["Empty package."]);
        }
        catch (JsonException ex)
        {
            throw new PackageValidationException([$"Not valid JSON: {ex.Message}"]);
        }

        var semanticErrors = CheckSemantics(dto);
        if (semanticErrors.Count > 0)
            throw new PackageValidationException(semanticErrors);

        return ToDomain(dto);
    }

    public string Write(KnowledgePackage package) => JsonSerializer.Serialize(FromDomain(package), JsonOptions);

    public async Task WriteFileAsync(KnowledgePackage package, string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(full), FileExtension, StringComparison.OrdinalIgnoreCase))
            full += FileExtension;
        if (Directory.Exists(full))
            throw new IOException("The export target is a directory.");

        // Write to a temp file then move, so a failed export never leaves a truncated package behind.
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        await File.WriteAllTextAsync(temp, Write(package), new UTF8Encoding(false), ct).ConfigureAwait(false);
        File.Move(temp, full, overwrite: true);
    }

    private static List<string> CheckSemantics(PackageDto p)
    {
        var errors = new List<string>();

        void Dupes(IEnumerable<string> ids, string what)
        {
            foreach (var g in ids.GroupBy(i => i, StringComparer.Ordinal).Where(g => g.Count() > 1))
                errors.Add($"Duplicate {what} id '{g.Key}'.");
        }

        Dupes(p.Sources.Select(s => s.Id), "source");
        Dupes(p.Entities.Select(e => e.Id), "entity");
        Dupes(p.Mechanics.Select(m => m.Id), "mechanic");
        Dupes((p.GameVersions ?? []).Select(v => v.Id), "game version");

        foreach (var m in p.Mechanics)
        {
            foreach (var seq in m.Sequences ?? [])
                foreach (var g in seq.Steps.GroupBy(s => s.Order).Where(g => g.Count() > 1))
                    errors.Add($"Mechanic '{m.Id}': step order {g.Key} used twice in one sequence.");
        }

        foreach (var d in AllDates(p).Where(d => d is not null && !DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            errors.Add($"Invalid date '{d}'.");

        return errors;
    }

    private static IEnumerable<string?> AllDates(PackageDto p)
    {
        yield return p.CreatedOn;
        foreach (var v in p.GameVersions ?? []) { yield return v.ReleaseDate; yield return v.VerifiedOn; }
        foreach (var s in p.Sources) { yield return s.ContentUpdatedOn; yield return s.RetrievedOn; yield return s.LastCheckedOn; }
        foreach (var m in p.Mechanics) yield return m.LastVerifiedOn;
    }

    // ------------------------------------------------------------- mapping

    private static DateOnly? D(string? s) => s is null ? null : DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string? S(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static VerificationStatus ParseVerification(string s) => s switch
    {
        "VERIFIED" => VerificationStatus.Verified,
        "COMMUNITY_REPORTED" => VerificationStatus.CommunityReported,
        "INFERRED" => VerificationStatus.Inferred,
        _ => VerificationStatus.Unknown,
    };

    internal static string FormatVerification(VerificationStatus v) => v switch
    {
        VerificationStatus.Verified => "VERIFIED",
        VerificationStatus.CommunityReported => "COMMUNITY_REPORTED",
        VerificationStatus.Inferred => "INFERRED",
        _ => "UNKNOWN",
    };

    private static Condition ToCondition(ConditionDto c) => new()
    {
        Kind = c.Kind is null ? ConditionKind.Manual : Enum.Parse<ConditionKind>(c.Kind),
        Description = c.Description,
        EntityId = c.EntityId,
        State = c.State,
        Number = c.Number,
    };

    private static ConditionDto FromCondition(Condition c) => new()
    {
        Kind = c.Kind.ToString(), Description = c.Description, EntityId = c.EntityId, State = c.State, Number = c.Number,
    };

    private static KnowledgePackage ToDomain(PackageDto p) => new()
    {
        PackageId = p.PackageId,
        Name = p.Name,
        Description = p.Description,
        Author = p.Author,
        CreatedOn = D(p.CreatedOn)!.Value,
        TestOnly = p.TestOnly ?? false,
        GameVersions = (p.GameVersions ?? []).Select(v => new GameVersion
        {
            Id = v.Id, Version = v.Version, ReleaseDate = D(v.ReleaseDate), SourceId = v.SourceId, VerifiedOn = D(v.VerifiedOn), Notes = v.Notes,
        }).ToList(),
        Sources = p.Sources.Select(s => new KnowledgeSource
        {
            Id = s.Id,
            Title = s.Title,
            Url = s.Url,
            Publisher = s.Publisher,
            Category = s.Category is null ? SourceCategory.Other : Enum.Parse<SourceCategory>(s.Category),
            Reliability = Enum.Parse<SourceReliability>(s.Reliability),
            ContentUpdatedOn = D(s.ContentUpdatedOn),
            RetrievedOn = D(s.RetrievedOn),
            LastCheckedOn = D(s.LastCheckedOn),
            Notes = s.Notes,
        }).ToList(),
        Entities = p.Entities.Select(e => new PackageEntity(
            new GameEntity
            {
                Id = e.Id,
                Type = Enum.Parse<EntityType>(e.Type),
                CanonicalName = e.CanonicalName.Trim(),
                Locale = e.Locale ?? "fr",
                Description = e.Description,
                Aliases = (e.Aliases ?? []).Select(a => new EntityAlias(a.Text.Trim(), a.Locale, RecordOrigin.Import)).ToList(),
                LevelText = e.LevelText,
                Attributes = e.Attributes ?? new Dictionary<string, string>(),
                Verification = ParseVerification(e.Verification),
                VersionScope = e.VersionScope,
                SourceIds = e.SourceIds ?? [],
                Origin = RecordOrigin.Import,
            },
            (e.Relations ?? []).Select(r => new EntityRelation(e.Id, Enum.Parse<RelationType>(r.Type), r.To)).ToList())).ToList(),
        Mechanics = p.Mechanics.Select(m => new Mechanic
        {
            Id = m.Id,
            EntityId = m.EntityId,
            Type = Enum.Parse<MechanicType>(m.Type),
            Title = m.Title,
            Summary = m.Summary,
            Verification = ParseVerification(m.Verification),
            Prerequisites = (m.Prerequisites ?? []).Select(ToCondition).ToList(),
            Sequences = (m.Sequences ?? []).Select(s => new ActionSequence
            {
                Label = s.Label,
                Steps = s.Steps.Select(st => new MechanicStep
                {
                    Order = st.Order,
                    Action = st.Action,
                    Target = st.Target,
                    Position = st.Position,
                    RequiredState = st.RequiredState,
                    When = st.When is null ? null : ToCondition(st.When),
                    Rationale = st.Rationale,
                    Verification = st.Verification is null ? null : ParseVerification(st.Verification),
                }).ToList(),
            }).ToList(),
            ExpectedStateChange = m.ExpectedStateChange,
            ExpectedResult = m.ExpectedResult,
            VulnerabilityDuration = m.VulnerabilityDuration,
            TurnConditions = m.TurnConditions,
            Notes = (m.Notes ?? []).Select(n => new MechanicNote(Enum.Parse<NoteKind>(n.Kind), n.Text)).ToList(),
            UnknownAspects = m.UnknownAspects ?? [],
            VersionScope = m.VersionScope,
            LastVerifiedOn = D(m.LastVerifiedOn),
            Sources = m.Sources.Select(s => new SourceReference(s.SourceId, s.Quote, s.Locator)).ToList(),
            Origin = RecordOrigin.Import,
        }).ToList(),
    };

    private static PackageDto FromDomain(KnowledgePackage p) => new()
    {
        Schema = "../schemas/knowledge-package.schema.json",
        FormatVersion = 1,
        PackageId = p.PackageId,
        Name = p.Name,
        Description = p.Description,
        Author = p.Author,
        CreatedOn = S(p.CreatedOn)!,
        TestOnly = p.TestOnly ? true : null,
        GameVersions = p.GameVersions.Select(v => new GameVersionDto
        {
            Id = v.Id, Version = v.Version, ReleaseDate = S(v.ReleaseDate), SourceId = v.SourceId, VerifiedOn = S(v.VerifiedOn), Notes = v.Notes,
        }).ToList(),
        Sources = p.Sources.Select(s => new SourceDto
        {
            Id = s.Id, Title = s.Title, Url = s.Url, Publisher = s.Publisher, Category = s.Category.ToString(),
            Reliability = s.Reliability.ToString(), ContentUpdatedOn = S(s.ContentUpdatedOn), RetrievedOn = S(s.RetrievedOn),
            LastCheckedOn = S(s.LastCheckedOn), Notes = s.Notes,
        }).ToList(),
        Entities = p.Entities.Select(pe => new EntityDto
        {
            Id = pe.Entity.Id,
            Type = pe.Entity.Type.ToString(),
            CanonicalName = pe.Entity.CanonicalName,
            Locale = pe.Entity.Locale,
            Description = pe.Entity.Description,
            Aliases = pe.Entity.Aliases.Count == 0 ? null : pe.Entity.Aliases.Select(a => new AliasDto { Text = a.Text, Locale = a.Locale }).ToList(),
            LevelText = pe.Entity.LevelText,
            Attributes = pe.Entity.Attributes.Count == 0 ? null : pe.Entity.Attributes.ToDictionary(),
            Verification = FormatVerification(pe.Entity.Verification),
            VersionScope = pe.Entity.VersionScope,
            SourceIds = pe.Entity.SourceIds.Count == 0 ? null : pe.Entity.SourceIds.ToList(),
            Relations = pe.Relations.Count == 0 ? null : pe.Relations.Select(r => new RelationDto { Type = r.Type.ToString(), To = r.ToId }).ToList(),
        }).ToList(),
        Mechanics = p.Mechanics.Select(m => new MechanicDto
        {
            Id = m.Id,
            EntityId = m.EntityId,
            Type = m.Type.ToString(),
            Title = m.Title,
            Summary = m.Summary,
            Verification = FormatVerification(m.Verification),
            Prerequisites = m.Prerequisites.Count == 0 ? null : m.Prerequisites.Select(FromCondition).ToList(),
            Sequences = m.Sequences.Count == 0 ? null : m.Sequences.Select(s => new SequenceDto
            {
                Label = s.Label,
                Steps = s.Steps.Select(st => new StepDto
                {
                    Order = st.Order, Action = st.Action, Target = st.Target, Position = st.Position, RequiredState = st.RequiredState,
                    When = st.When is null ? null : FromCondition(st.When), Rationale = st.Rationale,
                    Verification = st.Verification is { } v ? FormatVerification(v) : null,
                }).ToList(),
            }).ToList(),
            ExpectedStateChange = m.ExpectedStateChange,
            ExpectedResult = m.ExpectedResult,
            VulnerabilityDuration = m.VulnerabilityDuration,
            TurnConditions = m.TurnConditions,
            Notes = m.Notes.Count == 0 ? null : m.Notes.Select(n => new NoteDto { Kind = n.Kind.ToString(), Text = n.Text }).ToList(),
            UnknownAspects = m.UnknownAspects.Count == 0 ? null : m.UnknownAspects.ToList(),
            VersionScope = m.VersionScope,
            LastVerifiedOn = S(m.LastVerifiedOn),
            Sources = m.Sources.Select(s => new SourceRefDto { SourceId = s.SourceId, Quote = s.Quote, Locator = s.Locator }).ToList(),
        }).ToList(),
    };
}
