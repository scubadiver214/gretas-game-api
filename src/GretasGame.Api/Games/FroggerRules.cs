using GretasGame.Api.Scores;

namespace GretasGame.Api.Games;

/// <summary>
/// Plausibility limits for Frogger results. The numbers mirror the web game's
/// features/frogger/config.ts and levelConfig.ts; keep them in sync. The game
/// runs on the client, so this rejects impossible results rather than proving
/// a score was earned.
/// </summary>
public sealed class FroggerRules : ISessionGameRules
{
    public const int StartLives = 3;
    public const int ExtraLifeEvery = 10_000;
    public const int HomesPerLevel = 5;
    /// <summary>Rows between the start strip and the home row (each pays forward points once per trip).</summary>
    public const int RowsPerTrip = 12;
    public const int ForwardStepPoints = 10;
    public const int HomePoints = 50;
    public const int TimeBonusPerSecond = 10;
    public const int LevelCompletePoints = 1000;
    public const int MaxLevel = 99;
    public const double HopSeconds = 0.14;
    /// <summary>The client may report a little more play time than the server saw (clock skew, network).</summary>
    public const int DurationGraceSeconds = 5;
    public const int MaxDurationSeconds = 4 * 60 * 60;

    public string Mode => "frogger";

    public TimeSpan SessionLifetime => TimeSpan.FromSeconds(MaxDurationSeconds);

    /// <summary>Seconds each frog gets on a level (levelConfig.ts levelDef).</summary>
    public static int TimerSeconds(int level) => level switch
    {
        <= 1 => 60,
        2 => 55,
        3 => 50,
        _ => Math.Max(30, 50 - 3 * (level - 3)),
    };

    /// <summary>Most a single frog can earn reaching home on <paramref name="level"/>.</summary>
    public static int MaxHomeTrip(int level) =>
        RowsPerTrip * ForwardStepPoints + HomePoints + TimerSeconds(level) * TimeBonusPerSecond;

    /// <summary>
    /// Upper bound on a game's score: every cleared level at full marks, four of
    /// five homes on the level it ended on, and forward points on every trip that
    /// ended in a lost life.
    /// </summary>
    public static long MaxScore(int levelsCompleted, int livesLost)
    {
        long total = 0;
        for (var level = 1; level <= levelsCompleted; level++)
            total += HomesPerLevel * MaxHomeTrip(level) + LevelCompletePoints;
        total += (HomesPerLevel - 1) * MaxHomeTrip(levelsCompleted + 1);
        total += (long)livesLost * RowsPerTrip * ForwardStepPoints;
        return total;
    }

    /// <summary>Fastest possible level: five frogs, each hopping every row with no waiting.</summary>
    public static double MinSecondsPerLevel => HomesPerLevel * RowsPerTrip * HopSeconds;

    public void ValidateCompletion(CompleteSessionRequest r, TimeSpan serverElapsed, ValidationErrors errors)
    {
        if (r.Score < 0) errors.Add(nameof(r.Score), "Score cannot be negative.");
        // Every Frogger award is a multiple of 10.
        else if (r.Score % 10 != 0) errors.Add(nameof(r.Score), "Score must be a multiple of 10.");

        if (r.LevelsCompleted < 0 || r.LevelsCompleted >= MaxLevel)
            errors.Add(nameof(r.LevelsCompleted), $"LevelsCompleted must be between 0 and {MaxLevel - 1}.");
        if (r.HighestLevel != r.LevelsCompleted + 1)
            errors.Add(nameof(r.HighestLevel), "HighestLevel must be one more than LevelsCompleted.");

        var maxLivesLost = StartLives + Math.Max(0, r.Score) / ExtraLifeEvery;
        if (r.LivesLost < 0 || r.LivesLost > maxLivesLost)
            errors.Add(nameof(r.LivesLost), $"LivesLost must be between 0 and {maxLivesLost} for this score.");

        if (r.DurationSeconds < 1 || r.DurationSeconds > MaxDurationSeconds)
            errors.Add(nameof(r.DurationSeconds), $"DurationSeconds must be between 1 and {MaxDurationSeconds}.");
        else if (r.DurationSeconds > serverElapsed.TotalSeconds + DurationGraceSeconds)
            errors.Add(nameof(r.DurationSeconds), "DurationSeconds is longer than the session has been open.");

        if (errors.Count > 0) return;

        if (r.Score > MaxScore(r.LevelsCompleted, r.LivesLost))
            errors.Add(nameof(r.Score), "Score is higher than these levels allow.");
        if (serverElapsed.TotalSeconds < r.LevelsCompleted * MinSecondsPerLevel)
            errors.Add(nameof(r.LevelsCompleted), "Levels were completed faster than the frog can hop.");
    }
}
