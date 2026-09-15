namespace GretasGame.Api.Scores;

public sealed record SubmitScoreRequest(
    string Nickname,
    string Character,
    string Mode,
    int Score,
    int DurationSeconds,
    /// <summary>Avatar / outfit colour as #RRGGBB. Optional for older clients.</summary>
    string? Color = null,
    /// <summary>Difficulty level the round was played at (1-5). Defaults to 1.</summary>
    int Level = 1,
    /// <summary>UTC date key (yyyy-MM-dd) when the round was a daily challenge.</summary>
    string? ChallengeDate = null);

public sealed record SubmitScoreResponse(Guid Id, long Rank, bool IsPersonalBest);

public sealed record ScoreEntry(
    long Rank,
    string Nickname,
    string Character,
    string? Color,
    int Score,
    int Level,
    int DurationSeconds,
    DateTime CreatedAt);

/// <remarks><see cref="CreatedAt"/> is UTC (Npgsql maps timestamptz to a UTC DateTime).</remarks>
public sealed record PersonalBest(string Mode, int Score, DateTime CreatedAt);
