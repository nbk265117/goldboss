using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using DofusCompanion.App.Localization;
using DofusCompanion.App.Services;
using DofusCompanion.Application.Mechanics;
using DofusCompanion.Domain;

namespace DofusCompanion.App.ViewModels;

/// <summary>Visual category used by templates to pick colours/icons. Never conveys certainty by colour alone: a label is always shown too.</summary>
public enum Tone { Neutral, Good, Caution, Bad }

public sealed record ConditionLine(string Text, string ResultLabel, Tone Tone)
{
    /// <summary>"condition · result", or just the condition when its result is unknown (no "à vérifier" shown).</summary>
    public string Display => string.IsNullOrEmpty(ResultLabel) ? Text : $"{Text} · {ResultLabel}";
}

public sealed record StepLine(int Number, string Action, string? Details, ConditionLine? Applicability, string? Rationale, bool IsUnverified, string VerificationLabel);

public sealed record SequenceBlock(string? Label, IReadOnlyList<StepLine> Steps);

public sealed record NoteLine(string KindLabel, string Text, Tone Tone);

public sealed class SourceLine(KnowledgeSource source, string? quote, string? locator, SourceOpener opener)
{
    public string Title => source.Title;
    public string? Url => source.Url;
    public string? Publisher => source.Publisher;
    public string? Quote => quote;
    public string? Locator => locator;
    public string ReliabilityLabel => Loc.Instance[$"Reliability.{source.Reliability}"];
    public string? UpdatedOn => source.ContentUpdatedOn?.ToString("d", CultureInfo.CurrentCulture);
    public string? CheckedOn => source.LastCheckedOn?.ToString("d", CultureInfo.CurrentCulture);
    public bool CanOpen => SourceOpener.IsOpenable(source.Url);
    public IRelayCommand OpenCommand { get; } = new RelayCommand(() => opener.Open(source.Url), () => SourceOpener.IsOpenable(source.Url));
}

public sealed record MechanicCard
{
    /// <summary>Set when the card is shown on a dungeon's page: which of its bosses the mechanic belongs to.</summary>
    public string? BossName { get; init; }
    public required string Title { get; init; }
    public required string TypeLabel { get; init; }
    public required VerificationStatus Verification { get; init; }
    public required string VerificationLabel { get; init; }
    public required Tone VerificationTone { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<ConditionLine> Prerequisites { get; init; } = [];
    public IReadOnlyList<SequenceBlock> Sequences { get; init; } = [];
    public string? ExpectedResult { get; init; }
    public string? ExpectedStateChange { get; init; }
    public string? VulnerabilityDuration { get; init; }
    public string? TurnConditions { get; init; }
    public IReadOnlyList<NoteLine> Notes { get; init; } = [];
    public IReadOnlyList<string> Gaps { get; init; } = [];
    public IReadOnlyList<string> Staleness { get; init; } = [];
    public IReadOnlyList<SourceLine> Sources { get; init; } = [];
    public string? VersionScope { get; init; }
    public string? LastVerified { get; init; }
    public required string CompactText { get; init; }

    /// <summary>One link per source page (several quotes may come from the same page).</summary>
    public IReadOnlyList<SourceLine> DistinctSources => Sources.DistinctBy(x => x.Url ?? x.Title).ToList();
    /// <summary>Compact overlay: only the notes that can make the fight fail.</summary>
    public IReadOnlyList<NoteLine> KeyWarnings => Notes.Where(n => n.Tone == Tone.Bad).Take(3).ToList();
    public bool HasKeyWarnings => KeyWarnings.Count > 0;
    public bool HasPrerequisites => Prerequisites.Count > 0;
    public bool HasSequences => Sequences.Count > 0;
    public bool HasNotes => Notes.Count > 0;
    public bool HasGaps => Gaps.Count > 0;
    public bool HasStaleness => Staleness.Count > 0;
    public bool IsNotEstablished => false; // no "information non confirmée" banner for players
}

/// <summary>Maps engine output to display models. All wording comes from the localization table.</summary>
public static class GuidancePresenter
{
    private static Loc L => Loc.Instance;

    public static string TypeLabel(EntityType t) => L[$"EntityType.{t}"];

    public static string VerificationLabel(VerificationStatus v) => L[$"Verification.{v}"];

    /// <summary>Badge text shown to players: only "Vérifié"; other statuses show no badge (the user asked not to display "à vérifier" / "rapporté par la communauté").</summary>
    public static string BadgeLabel(VerificationStatus v) => v == VerificationStatus.Verified ? VerificationLabel(v) : "";

    public static Tone VerificationTone(VerificationStatus v) => v switch
    {
        VerificationStatus.Verified => Tone.Good,
        VerificationStatus.CommunityReported => Tone.Caution,
        _ => Tone.Bad,
    };

    public static ConditionLine Condition(EvaluatedCondition c) => new(
        c.Condition.Description,
        c.Result == ConditionResult.Unknown ? "" : L[$"Condition.{c.Result}"],
        c.Result switch { ConditionResult.Satisfied => Tone.Good, ConditionResult.NotSatisfied => Tone.Bad, _ => Tone.Caution });

    public static MechanicCard Card(MechanicGuidance g, SourceOpener opener, string? bossName = null)
    {
        var m = g.Mechanic;
        var sequences = g.Sequences.Select(s => new SequenceBlock(s.Label, s.Steps.Select(Step).ToList())).ToList();
        var card = new MechanicCard
        {
            BossName = bossName,
            Title = m.Title,
            TypeLabel = L[$"MechanicType.{m.Type}"],
            Verification = m.Verification,
            VerificationLabel = BadgeLabel(m.Verification),
            VerificationTone = VerificationTone(m.Verification),
            Summary = m.Summary,
            Prerequisites = g.Prerequisites.Select(Condition).ToList(),
            Sequences = sequences,
            ExpectedResult = m.ExpectedResult,
            ExpectedStateChange = m.ExpectedStateChange,
            VulnerabilityDuration = m.VulnerabilityDuration,
            TurnConditions = m.TurnConditions,
            Notes = m.Notes.Select(n => new NoteLine(L[$"NoteKind.{n.Kind}"], n.Text,
                n.Kind is NoteKind.FailureCase ? Tone.Bad : n.Kind is NoteKind.Exception ? Tone.Neutral : Tone.Caution)).ToList(),
            Gaps = g.Gaps.Select(Gap).ToList(),
            Staleness = [], // "pas revérifié depuis…" warnings are not shown to players
            Sources = g.Sources.Select(s => new SourceLine(s.Source, s.Quote, s.Locator, opener)).ToList(),
            VersionScope = m.VersionScope,
            LastVerified = m.LastVerifiedOn?.ToString("d", CultureInfo.CurrentCulture),
            CompactText = "",
        };
        return card with { CompactText = Compact(g, card) };
    }

    private static StepLine Step(GuidanceStep s)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Source.Target)) details.Add($"{L["Step.Target"]} {s.Source.Target}");
        if (!string.IsNullOrWhiteSpace(s.Source.Position)) details.Add($"{L["Step.Position"]} {s.Source.Position}");
        if (!string.IsNullOrWhiteSpace(s.Source.RequiredState)) details.Add($"{L["Step.State"]} {s.Source.RequiredState}");
        return new StepLine(
            s.Number,
            s.Source.Action,
            details.Count == 0 ? null : string.Join(" · ", details),
            s.Applicability is null ? null : Condition(s.Applicability),
            s.Source.Rationale,
            false,
            BadgeLabel(s.Verification));
    }

    private static string Gap(KnowledgeGap g) => g.Kind == GapKind.Documented ? g.Text ?? "" : L[$"Gap.{g.Kind}"];

    private static string Stale(StalenessWarning w) => L.Format($"Stale.{w.Reason}", w.Detail);

    /// <summary>The ordered, plain-text block used by the overlay's compact mode (and copy-to-clipboard).</summary>
    private static string Compact(MechanicGuidance g, MechanicCard c)
    {
        var sb = new StringBuilder();
        if (c.BossName is not null) sb.Append(L["Compact.Boss"]).Append(' ').AppendLine(c.BossName);
        sb.Append(L["Compact.Objective"]).Append(' ').AppendLine(c.Title);
        if (c.VerificationLabel.Length > 0) sb.Append(L["Compact.Status"]).Append(' ').AppendLine(c.VerificationLabel);
        foreach (var p in c.Prerequisites)
            sb.Append(L["Compact.Prerequisite"]).Append(' ').AppendLine(p.ResultLabel.Length > 0 ? $"{p.Text} ({p.ResultLabel})" : p.Text);
        var first = c.Sequences.FirstOrDefault();
        if (first is not null)
        {
            if (c.Sequences.Count > 1 && first.Label is not null) sb.Append("[").Append(first.Label).AppendLine("]");
            foreach (var st in first.Steps)
                sb.Append(L.Format("Compact.Step", st.Number)).Append(' ').AppendLine(st.Action);
            if (c.Sequences.Count > 1) sb.AppendLine(L.Format("Compact.Alternatives", c.Sequences.Count - 1));
        }

        if (c.ExpectedResult is not null) sb.Append(L["Compact.Expected"]).Append(' ').AppendLine(c.ExpectedResult);
        if (c.VulnerabilityDuration is not null) sb.Append(L["Compact.Duration"]).Append(' ').AppendLine(c.VulnerabilityDuration);
        foreach (var n in c.Notes.Where(n => n.Tone == Tone.Bad).Take(2))
            sb.Append(L["Compact.Warning"]).Append(' ').AppendLine(n.Text);
        foreach (var gap in c.Gaps.Take(2))
            sb.Append(L["Compact.Unknown"]).Append(' ').AppendLine(gap);
        foreach (var s in c.Staleness.Take(1))
            sb.Append(L["Compact.Outdated"]).Append(' ').AppendLine(s);
        var src = g.Sources.FirstOrDefault();
        if (src is not null) sb.Append(L["Compact.Source"]).Append(' ').Append(src.Source.Title).Append(" — ").Append(src.Source.Publisher);
        return sb.ToString().TrimEnd();
    }
}
