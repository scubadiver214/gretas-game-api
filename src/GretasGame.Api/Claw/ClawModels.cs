namespace GretasGame.Api.Claw;

public sealed record StartAttemptRequest(
    string Character,
    /// <summary>Avatar colour as #RRGGBB.</summary>
    string? Color,
    /// <summary>Level to play (regular attempts). Must be unlocked.</summary>
    int Level,
    /// <summary>World id when playing that world's mystery machine; the level is then the world's last level.</summary>
    string? Mystery = null);

/// <remarks><see cref="StartedAt"/> is UTC.</remarks>
public sealed record StartAttemptResponse(Guid AttemptId, DateTime StartedAt, int Level, string Kind);

public sealed record RecordGrabRequest(
    string Nickname,
    /// <summary>1-based grab number within the attempt; the same number is only ever recorded once.</summary>
    int Seq,
    /// <summary>The prize that was caught; required when <see cref="Succeeded"/>.</summary>
    string? PrizeId,
    bool Succeeded,
    /// <summary>Active play seconds since the attempt started, as the client saw them.</summary>
    int ElapsedSeconds);

public sealed record RecordGrabResponse(
    bool Succeeded,
    string? PrizeId,
    /// <summary>How many of this prize the player now owns (0 for a miss).</summary>
    int Count,
    int Points,
    long TotalPoints,
    IReadOnlyList<string> NewAvatars);

public sealed record CompleteAttemptRequest(string Nickname, int DurationSeconds);

public sealed record CompleteAttemptResponse(
    /// <summary>0 when the attempt ended before the goal was met.</summary>
    int Stars,
    bool Cleared,
    int CurrentLevel,
    IReadOnlyList<string> NewAvatars,
    /// <summary>Set when this clear opened a world's mystery machine.</summary>
    string? MysteryUnlocked);

public sealed record SelectAvatarRequest(string Character, string? Color, string AvatarId);

public sealed record FavoriteRequest(bool Favorite);

public sealed record ClawPrizeEntry(string PrizeId, int Count, bool Favorite, DateTime FirstCollectedAt, DateTime LastCollectedAt);

public sealed record ClawAvatarEntry(string AvatarId, DateTime UnlockedAt);

public sealed record ClawLevelEntry(int Level, int Stars, int BestGrabs, int Clears, DateTime LastClearedAt);

public sealed record ClawProfile(
    string Nickname,
    int CurrentLevel,
    string? SelectedAvatar,
    long Points,
    int TotalGrabs,
    int SuccessfulGrabs,
    IReadOnlyList<ClawPrizeEntry> Prizes,
    IReadOnlyList<ClawAvatarEntry> Avatars,
    IReadOnlyList<ClawLevelEntry> Levels,
    /// <summary>Worlds whose mystery prize has been won.</summary>
    IReadOnlyList<string> MysteryWorldsWon)
{
    public static ClawProfile Empty(string nickname) => new(nickname, 1, null, 0, 0, 0, [], [], [], []);
}
