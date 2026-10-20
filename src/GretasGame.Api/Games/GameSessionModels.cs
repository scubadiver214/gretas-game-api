namespace GretasGame.Api.Games;

public sealed record StartSessionRequest(
    string Nickname,
    string Character,
    /// <summary>Avatar colour as #RRGGBB.</summary>
    string? Color = null,
    /// <summary>UTC date key (yyyy-MM-dd) when playing that day's daily challenge.</summary>
    string? ChallengeDate = null);

/// <remarks><see cref="StartedAt"/> is UTC.</remarks>
public sealed record StartSessionResponse(Guid SessionId, DateTime StartedAt);

public sealed record CompleteSessionRequest(
    string Nickname,
    int Score,
    int HighestLevel,
    int LevelsCompleted,
    int LivesLost,
    /// <summary>Active play time reported by the client (pauses excluded).</summary>
    int DurationSeconds);

public sealed record CompleteSessionResponse(long Rank, bool IsPersonalBest, int? PreviousBest);

public sealed record PlayerGameStats(int GamesPlayed, int HighestScore, int HighestLevel, long TotalScore, DateTime? LastPlayedAt);

public sealed record GameHistoryEntry(
    Guid SessionId,
    int Score,
    int HighestLevel,
    int LevelsCompleted,
    int LivesLost,
    int DurationSeconds,
    /// <summary>yyyy-MM-dd when the game was a daily challenge.</summary>
    string? ChallengeDate,
    DateTime CompletedAt);
