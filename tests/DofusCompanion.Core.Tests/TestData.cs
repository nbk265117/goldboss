using DofusCompanion.Domain;

namespace DofusCompanion.Core.Tests;

/// <summary>
/// SYNTHETIC, TEST-ONLY fixtures. These names and mechanics are invented for unit tests and must never be
/// shipped as game knowledge.
/// </summary>
internal static class TestData
{
    public static GameEntity Entity(string id, string name, EntityType type = EntityType.Boss, params string[] aliases) => new()
    {
        Id = id,
        Type = type,
        CanonicalName = name,
        Aliases = aliases.Select(a => new EntityAlias(a)).ToList(),
        Verification = VerificationStatus.Verified,
    };

    public static KnowledgeSource Source(string id = "src.test", DateOnly? updated = null) => new()
    {
        Id = id,
        Title = "Test source",
        Url = "https://example.invalid/test",
        Reliability = SourceReliability.ReputableGuide,
        ContentUpdatedOn = updated,
    };

    public static Mechanic Mechanic(string id = "mech.test", string entityId = "boss.test", VerificationStatus v = VerificationStatus.Verified) => new()
    {
        Id = id,
        EntityId = entityId,
        Type = MechanicType.Invulnerability,
        Title = "Test mechanic",
        Verification = v,
        ExpectedResult = "Test result",
        VulnerabilityDuration = "1 tour",
        LastVerifiedOn = new DateOnly(2026, 10, 1),
        Sequences = [new ActionSequence { Steps = [new MechanicStep { Order = 1, Action = "Do the thing" }] }],
        Sources = [new SourceReference("src.test", "quote")],
    };
}
