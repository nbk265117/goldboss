using DofusCompanion.Application.Matching;
using DofusCompanion.Domain;

namespace DofusCompanion.Core.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("Kralamoure Géant", "kralamoure geant")]
    [InlineData("  KRALAMOURE   GÉANT !! ", "kralamoure geant")]
    [InlineData("Fabrique de foux d'artifice", "fabrique de foux d artifice")]
    [InlineData("Fabrique de foux d’artifice", "fabrique de foux d artifice")]
    [InlineData("Œil de Vortex", "oeil de vortex")]
    [InlineData("Trône de sang", "trone de sang")]
    [InlineData("Ｋｉｍｂｏ", "kimbo")] // full-width compatibility characters
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("!!!", "")]
    public void Normalize_produces_canonical_form(string? input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void Composed_and_decomposed_accents_are_equal() =>
        Assert.Equal(TextNormalizer.Normalize("Géant"), TextNormalizer.Normalize("Géant"));
}

public class StringSimilarityTests
{
    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("abc", "abc", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("ab", "ba", 1)] // adjacent transposition counts once
    public void EditDistance_is_optimal_string_alignment(string a, string b, int expected) =>
        Assert.Equal(expected, StringSimilarity.EditDistance(a, b));

    [Theory]
    [InlineData("kralamoure", "kralamoure", 2, 0)]
    [InlineData("kitten", "sitting", 3, 3)]
    [InlineData("kitten", "sitting", 2, 3)] // capped at max + 1
    [InlineData("abc", "abcdefgh", 2, 3)] // length difference alone exceeds max
    public void BoundedEditDistance_caps_at_max_plus_one(string a, string b, int max, int expected) =>
        Assert.Equal(expected, StringSimilarity.BoundedEditDistance(a, b, max));

    [Fact]
    public void RatioAtLeast_returns_zero_below_threshold()
    {
        Assert.Equal(0, StringSimilarity.RatioAtLeast("abcdef", "uvwxyz", 0.75));
        Assert.Equal(StringSimilarity.Ratio("kralamoure", "kralamour"), StringSimilarity.RatioAtLeast("kralamoure", "kralamour", 0.75), 6);
    }

    [Fact]
    public void Word_order_does_not_matter_for_score() =>
        Assert.Equal(1.0, StringSimilarity.Score("geant kralamoure", "kralamoure geant"), 3);

    [Fact]
    public void One_ocr_error_scores_high_but_below_exact()
    {
        var score = StringSimilarity.Score("kralamoure geamt", "kralamoure geant");
        Assert.InRange(score, 0.9, 0.99);
    }

    [Fact]
    public void Long_strings_beyond_stack_threshold_work()
    {
        var a = new string('a', 400);
        var b = new string('a', 399) + "b";
        Assert.Equal(1, StringSimilarity.EditDistance(a, b));
    }
}

public class EntityMatcherTests
{
    private static EntityMatcher Matcher(MatchingOptions? o = null) => new(
    [
        TestData.Entity("boss.alpha", "Gardien Alpha", EntityType.Boss, "Alpha"),
        TestData.Entity("boss.beta", "Gardien Beta"),
        TestData.Entity("mon.gelee", "Gelée Bleue", EntityType.Monster),
        TestData.Entity("mon.geleer", "Gelée Rouge", EntityType.Monster),
        TestData.Entity("dj.crypte", "Crypte Oubliée", EntityType.Dungeon),
        TestData.Entity("mon.pou", "Pou", EntityType.Monster),
    ], o);

    [Fact]
    public void Exact_name_is_confident()
    {
        var r = Matcher().Match("Gardien Alpha");
        Assert.Equal(MatchOutcome.Confident, r.Outcome);
        Assert.Equal("boss.alpha", r.Best!.Entity.Id);
        Assert.Equal(1.0, r.Best.Score);
    }

    [Fact]
    public void Alias_matches_entity()
    {
        var r = Matcher().Match("alpha");
        Assert.Equal("boss.alpha", r.Best!.Entity.Id);
    }

    [Fact]
    public void Accent_and_case_insensitive()
    {
        var r = Matcher().Match("GELEE BLEUE");
        Assert.Equal(MatchOutcome.Confident, r.Outcome);
        Assert.Equal("mon.gelee", r.Best!.Entity.Id);
    }

    [Fact]
    public void Equally_close_candidates_are_ambiguous_not_auto_selected()
    {
        // "Gardien Xeta" is exactly one substitution away from both "Gardien Beta" and "Gardien Zeta".
        var matcher = new EntityMatcher([TestData.Entity("b", "Gardien Beta"), TestData.Entity("z", "Gardien Zeta")]);
        var r = matcher.Match("Gardien Xeta");
        Assert.Equal(MatchOutcome.Ambiguous, r.Outcome);
        Assert.Equal(2, r.Candidates.Count);
    }

    [Fact]
    public void One_edit_on_a_long_name_is_confident()
    {
        var r = Matcher().Match("Crypte Ouliée"); // 1 edit over 14 chars -> 0.93
        Assert.Equal(MatchOutcome.Confident, r.Outcome);
        Assert.Equal("dj.crypte", r.Best!.Entity.Id);
    }

    [Fact]
    public void Fuzzy_match_below_auto_accept_needs_confirmation()
    {
        var r = Matcher().Match("Crypte Oulie"); // 2 edits over 14 chars -> 0.86
        Assert.Equal(MatchOutcome.NeedsConfirmation, r.Outcome);
        Assert.Equal("dj.crypte", r.Best!.Entity.Id);
        Assert.InRange(r.Best.Score, 0.75, 0.92);
    }

    [Fact]
    public void Unrelated_text_is_no_match()
    {
        var r = Matcher().Match("Bonjour tout le monde");
        Assert.Equal(MatchOutcome.NoMatch, r.Outcome);
        Assert.Empty(r.Candidates);
    }

    [Fact]
    public void Short_names_require_exact_match()
    {
        Assert.Equal("mon.pou", Matcher().Match("pou").Best!.Entity.Id);
        Assert.Equal(MatchOutcome.NoMatch, Matcher().Match("pon").Outcome);
    }

    [Fact]
    public void FindInText_extracts_names_from_noisy_ocr_lines()
    {
        var found = Matcher().FindInText(["Niv. 200 Gardien Alpha (Boss)", "PA 6 PM 3", "Gelée Bleue niv 10"]);
        Assert.Contains(found, c => c.Entity.Id == "boss.alpha");
        Assert.Contains(found, c => c.Entity.Id == "mon.gelee");
        Assert.DoesNotContain(found, c => c.Entity.Id == "dj.crypte");
    }

    [Fact]
    public void Thresholds_are_configurable()
    {
        var strict = Matcher(new MatchingOptions { MinCandidateScore = 0.99 });
        Assert.Equal(MatchOutcome.NoMatch, strict.Match("Crypte Ouliée").Outcome);
    }

    [Fact]
    public void Filter_restricts_candidates()
    {
        var r = Matcher().Match("Gardien Alpha", e => e.Type == EntityType.Monster);
        Assert.DoesNotContain(r.Candidates, c => c.Entity.Id == "boss.alpha");
    }

    [Fact]
    public void Candidates_are_ranked_by_score()
    {
        var found = Matcher().FindInText(["Gardien Betaa"]);
        Assert.Equal("boss.beta", found[0].Entity.Id);
        Assert.True(found.SequenceEqual(found.OrderByDescending(c => c.Score)));
    }
}
