namespace GretasGame.Api.Scores;

public sealed record SubmitScoreRequest(
    string Nickname,
    string Character,
    string Mode,
    int Score,
    int DurationSeconds);

public sealed record SubmitScoreResponse(Guid Id, long Rank, bool IsPersonalBest);

public sealed record ScoreEntry(
    long Rank,
    string Nickname,
    string Character,
    int Score,
    int DurationSeconds,
    DateTime CreatedAt);

/// <remarks><see cref="CreatedAt"/> is UTC (Npgsql maps timestamptz to a UTC DateTime).</remarks>
public sealed record PersonalBest(string Mode, int Score, DateTime CreatedAt);
