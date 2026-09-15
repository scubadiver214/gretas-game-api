using GretasGame.Api.Scores;

namespace GretasGame.Api.Tests;

public class ScoreRulesTests
{
    private static SubmitScoreRequest Valid() => new("Greta", "girl", "kitchen", 120, 45);

    [Fact]
    public void Valid_request_has_no_errors()
    {
        Assert.Empty(ScoreRules.Validate(Valid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("G")]
    [InlineData("   ")]
    [InlineData("ThisNicknameIsWayTooLong")]
    [InlineData("bad<script>")]
    public void Rejects_bad_nicknames(string nickname)
    {
        var errors = ScoreRules.Validate(Valid() with { Nickname = nickname });
        Assert.Contains(nameof(SubmitScoreRequest.Nickname), errors.Keys);
    }

    [Theory]
    [InlineData("Greta")]
    [InlineData("Max 2")]
    [InlineData("Zoë")]
    [InlineData("pizza_fan-1")]
    public void Accepts_friendly_nicknames(string nickname)
    {
        Assert.Empty(ScoreRules.Validate(Valid() with { Nickname = nickname }));
    }

    [Fact]
    public void Normalizes_whitespace_and_key_is_case_insensitive()
    {
        var normalized = ScoreRules.NormalizeNickname("  Greta   Pizza ");
        Assert.Equal("Greta Pizza", normalized);
        Assert.Equal(ScoreRules.NicknameKey("GRETA"), ScoreRules.NicknameKey("greta"));
    }

    [Theory]
    [InlineData("robot")]
    [InlineData("")]
    public void Rejects_unknown_characters(string character)
    {
        var errors = ScoreRules.Validate(Valid() with { Character = character });
        Assert.Contains(nameof(SubmitScoreRequest.Character), errors.Keys);
    }

    [Theory]
    [InlineData("racing")]
    [InlineData("Kitchen")]
    public void Rejects_unknown_modes(string mode)
    {
        var errors = ScoreRules.Validate(Valid() with { Mode = mode });
        Assert.Contains(nameof(SubmitScoreRequest.Mode), errors.Keys);
    }

    [Theory]
    [InlineData("kitchen", -1, false)]
    [InlineData("kitchen", 0, true)]
    [InlineData("kitchen", 1000, true)]
    [InlineData("kitchen", 1001, false)]
    [InlineData("delivery", 5000, true)]
    [InlineData("delivery", 99999, false)]
    public void Bounds_scores_per_mode(string mode, int score, bool ok)
    {
        var errors = ScoreRules.Validate(Valid() with { Mode = mode, Score = score });
        Assert.Equal(ok, !errors.ContainsKey(nameof(SubmitScoreRequest.Score)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(600, true)]
    [InlineData(601, false)]
    public void Bounds_duration(int seconds, bool ok)
    {
        var errors = ScoreRules.Validate(Valid() with { DurationSeconds = seconds });
        Assert.Equal(ok, !errors.ContainsKey(nameof(SubmitScoreRequest.DurationSeconds)));
    }

    [Theory]
    [InlineData(null, ScoreRules.DefaultTopLimit)]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(1000, ScoreRules.MaxTopLimit)]
    public void Clamps_top_limit(int? requested, int expected)
    {
        Assert.Equal(expected, ScoreRules.ClampLimit(requested));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("#F2542D", true)]
    [InlineData("#abcdef", true)]
    [InlineData("F2542D", false)]
    [InlineData("#F2542", false)]
    [InlineData("red", false)]
    public void Validates_optional_color(string? color, bool ok)
    {
        var errors = ScoreRules.Validate(Valid() with { Color = color });
        Assert.Equal(ok, !errors.ContainsKey(nameof(SubmitScoreRequest.Color)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void Bounds_level(int level, bool ok)
    {
        var errors = ScoreRules.Validate(Valid() with { Level = level });
        Assert.Equal(ok, !errors.ContainsKey(nameof(SubmitScoreRequest.Level)));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("2026-09-15", true)]  // today
    [InlineData("2026-09-14", true)]  // yesterday: still inside the grace window
    [InlineData("2026-09-13", false)] // closed
    [InlineData("2026-09-16", false)] // not yet open
    [InlineData("15/09/2026", false)] // wrong format
    [InlineData("nonsense", false)]
    public void Validates_challenge_date_against_today(string? challengeDate, bool ok)
    {
        var today = new DateOnly(2026, 9, 15);
        var errors = ScoreRules.Validate(Valid() with { ChallengeDate = challengeDate }, today);
        Assert.Equal(ok, !errors.ContainsKey(nameof(SubmitScoreRequest.ChallengeDate)));
    }

    [Fact]
    public void Parses_challenge_dates_strictly()
    {
        Assert.True(ScoreRules.TryParseChallengeDate("2026-01-05", out var d));
        Assert.Equal(new DateOnly(2026, 1, 5), d);
        Assert.False(ScoreRules.TryParseChallengeDate("2026-1-5", out _));
        Assert.False(ScoreRules.TryParseChallengeDate(null, out _));
    }
}
