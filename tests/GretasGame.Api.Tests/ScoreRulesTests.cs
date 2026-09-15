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
    [InlineData("kitchen", 300, true)]
    [InlineData("kitchen", 301, false)]
    [InlineData("delivery", 3000, true)]
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
}
