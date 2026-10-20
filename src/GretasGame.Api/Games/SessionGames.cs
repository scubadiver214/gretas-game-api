using GretasGame.Api.Scores;

namespace GretasGame.Api.Games;

/// <summary>Per-game rules for games that play through a server-tracked session.</summary>
public interface ISessionGameRules
{
    /// <summary>Score mode written to <c>scores.mode</c> and <c>game_sessions.mode</c>.</summary>
    string Mode { get; }

    /// <summary>Sessions older than this can no longer be completed.</summary>
    TimeSpan SessionLifetime { get; }

    /// <summary>
    /// Plausibility checks for a finished game. <paramref name="serverElapsed"/> is
    /// wall time since the session started, measured by the server.
    /// </summary>
    void ValidateCompletion(CompleteSessionRequest request, TimeSpan serverElapsed, ValidationErrors errors);
}

/// <summary>
/// Games that use sessions. Adding another arcade game means a rules class
/// here, its mode in the game_sessions/scores check constraints, and nothing else.
/// </summary>
public static class SessionGames
{
    private static readonly IReadOnlyDictionary<string, ISessionGameRules> Registry =
        new Dictionary<string, ISessionGameRules>(StringComparer.Ordinal)
        {
            ["frogger"] = new FroggerRules(),
        };

    public static bool TryGet(string game, out ISessionGameRules rules) =>
        Registry.TryGetValue(game, out rules!);
}
