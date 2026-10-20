using GretasGame.Api.Games;
using GretasGame.Api.Scores;

namespace GretasGame.Api.Tests;

public class FroggerRulesTests
{
    private static readonly FroggerRules Rules = new();

    private static CompleteSessionRequest Valid() => new("Greta", Score: 1_250, HighestLevel: 2, LevelsCompleted: 1, LivesLost: 3, DurationSeconds: 120);

    private static IReadOnlyDictionary<string, string[]> Check(CompleteSessionRequest r, double serverSeconds = 130)
    {
        var errors = new ValidationErrors();
        Rules.ValidateCompletion(r, TimeSpan.FromSeconds(serverSeconds), errors);
        return errors.ToDictionary();
    }

    [Fact]
    public void Plausible_game_passes()
    {
        Assert.Empty(Check(Valid()));
    }

    [Fact]
    public void Timer_mirrors_the_web_level_config()
    {
        Assert.Equal(60, FroggerRules.TimerSeconds(1));
        Assert.Equal(55, FroggerRules.TimerSeconds(2));
        Assert.Equal(50, FroggerRules.TimerSeconds(3));
        Assert.Equal(47, FroggerRules.TimerSeconds(4));
        Assert.Equal(30, FroggerRules.TimerSeconds(40));
    }

    [Fact]
    public void Max_score_allows_a_perfect_first_level()
    {
        // Level 1: 5 homes x (12 rows x 10 + 50 + 60 s x 10) + 1000.
        var perfectLevel1 = 5 * (120 + 50 + 600) + 1000;
        Assert.True(FroggerRules.MaxScore(levelsCompleted: 1, livesLost: 0) >= perfectLevel1);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(1_255)]
    public void Rejects_negative_or_non_multiple_of_ten_scores(int score)
    {
        Assert.Contains(nameof(CompleteSessionRequest.Score), Check(Valid() with { Score = score }).Keys);
    }

    [Fact]
    public void Rejects_scores_higher_than_the_levels_allow()
    {
        var max = FroggerRules.MaxScore(1, 3);
        Assert.Empty(Check(Valid() with { Score = (int)(max / 10 * 10) }));
        Assert.Contains(nameof(CompleteSessionRequest.Score), Check(Valid() with { Score = (int)max + 10 }).Keys);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(0, -1)]
    public void HighestLevel_must_follow_levels_completed(int highestLevel, int levelsCompleted)
    {
        var errors = Check(Valid() with { HighestLevel = highestLevel, LevelsCompleted = levelsCompleted });
        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData(-1, 1_250, false)]
    [InlineData(3, 1_250, true)]
    [InlineData(4, 1_250, false)]
    // Each 10,000 points earns an extra life to lose.
    [InlineData(4, 10_000, true)]
    public void Bounds_lives_lost_by_extra_lives_earned(int livesLost, int score, bool ok)
    {
        var r = Valid() with { LivesLost = livesLost, Score = score, LevelsCompleted = 3, HighestLevel = 4 };
        var errors = Check(r, serverSeconds: 600);
        Assert.Equal(ok, !errors.ContainsKey(nameof(CompleteSessionRequest.LivesLost)));
    }

    [Fact]
    public void Rejects_client_durations_longer_than_the_session()
    {
        Assert.Empty(Check(Valid() with { DurationSeconds = 134 }, serverSeconds: 130));
        Assert.Contains(nameof(CompleteSessionRequest.DurationSeconds), Check(Valid() with { DurationSeconds = 200 }, serverSeconds: 130).Keys);
        Assert.Contains(nameof(CompleteSessionRequest.DurationSeconds), Check(Valid() with { DurationSeconds = 0 }).Keys);
    }

    [Fact]
    public void Rejects_levels_cleared_faster_than_the_frog_can_hop()
    {
        var tooFast = FroggerRules.MinSecondsPerLevel * 3 - 1;
        var r = Valid() with { LevelsCompleted = 3, HighestLevel = 4, Score = 5_000, DurationSeconds = 10 };
        Assert.Contains(nameof(CompleteSessionRequest.LevelsCompleted), Check(r, tooFast).Keys);
        Assert.Empty(Check(r, FroggerRules.MinSecondsPerLevel * 3 + 1));
    }
}
