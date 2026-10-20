using Dapper;
using GretasGame.Api.Players;
using GretasGame.Api.Scores;
using Npgsql;

namespace GretasGame.Api.Games;

public sealed record NewSession(string Nickname, string Character, string? Color, DateOnly? ChallengeDate);

public enum CompleteOutcome
{
    Completed,
    NotFound,
    Forbidden,
    Expired,
    Invalid,
    Conflict,
}

public sealed record CompleteResult(CompleteOutcome Outcome, CompleteSessionResponse? Response = null, IReadOnlyDictionary<string, string[]>? Errors = null);

public interface IGameSessionRepository
{
    Task<StartSessionResponse> StartAsync(string mode, NewSession session, CancellationToken ct);

    /// <summary>
    /// Finishes a session and records its score, all in one transaction.
    /// Repeating an identical completion returns the original result instead of a second score.
    /// </summary>
    Task<CompleteResult> CompleteAsync(ISessionGameRules rules, Guid sessionId, CompleteSessionRequest request, CancellationToken ct);

    Task<PlayerGameStats> GetStatsAsync(string mode, string nickname, CancellationToken ct);
    Task<IReadOnlyList<GameHistoryEntry>> GetHistoryAsync(string mode, string nickname, int limit, CancellationToken ct);
}

public sealed class GameSessionRepository(NpgsqlDataSource dataSource) : IGameSessionRepository
{
    private sealed record SessionRow(
        Guid Id,
        Guid PlayerId,
        string NicknameNormalized,
        string Status,
        DateTime StartedAt,
        /// <summary>yyyy-MM-dd text: avoids depending on how the driver surfaces <c>date</c>.</summary>
        string? ChallengeDateKey,
        int? FinalScore,
        int? HighestLevel,
        int? LevelsCompleted,
        int? LivesLost,
        int? DurationSeconds,
        DateTime Now)
    {
        public DateOnly? ChallengeDate => ScoreRules.TryParseChallengeDate(ChallengeDateKey, out var d) ? d : null;
    }

    public async Task<StartSessionResponse> StartAsync(string mode, NewSession s, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var playerId = await PlayerSql.UpsertAsync(conn, tx, s.Nickname, s.Character, s.Color, ct);
        var id = Guid.CreateVersion7();
        var startedAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            """
            INSERT INTO game_sessions (id, player_id, mode, status, challenge_date, started_at)
            VALUES (@Id, @PlayerId, @Mode, 'active', @ChallengeDate, now())
            RETURNING started_at;
            """,
            new { Id = id, PlayerId = playerId, Mode = mode, s.ChallengeDate }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new StartSessionResponse(id, startedAt);
    }

    public async Task<CompleteResult> CompleteAsync(ISessionGameRules rules, Guid sessionId, CompleteSessionRequest r, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Row lock: concurrent duplicate completions queue up here and see "completed".
        var session = await conn.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(
            """
            SELECT s.id, s.player_id, p.nickname_normalized, s.status, s.started_at, to_char(s.challenge_date, 'YYYY-MM-DD') AS challenge_date_key,
                   s.final_score, s.highest_level, s.levels_completed, s.lives_lost, s.duration_seconds,
                   now() AS now
            FROM game_sessions s
            JOIN players p ON p.id = s.player_id
            WHERE s.id = @Id AND s.mode = @Mode
            FOR UPDATE OF s;
            """,
            new { Id = sessionId, rules.Mode }, tx, cancellationToken: ct));

        if (session is null) return new CompleteResult(CompleteOutcome.NotFound);

        var key = ScoreRules.NicknameKey(ScoreRules.NormalizeNickname(r.Nickname ?? string.Empty));
        if (!string.Equals(session.NicknameNormalized, key, StringComparison.Ordinal))
            return new CompleteResult(CompleteOutcome.Forbidden);

        if (session.Status == "completed")
        {
            var same = session.FinalScore == r.Score && session.HighestLevel == r.HighestLevel
                && session.LevelsCompleted == r.LevelsCompleted && session.LivesLost == r.LivesLost
                && session.DurationSeconds == r.DurationSeconds;
            if (!same) return new CompleteResult(CompleteOutcome.Conflict);
            var replay = await ReplayResultAsync(conn, tx, rules.Mode, session, ct);
            await tx.CommitAsync(ct);
            return new CompleteResult(CompleteOutcome.Completed, replay);
        }

        var elapsed = session.Now - session.StartedAt;
        if (elapsed > rules.SessionLifetime) return new CompleteResult(CompleteOutcome.Expired);

        var errors = new ValidationErrors();
        rules.ValidateCompletion(r, elapsed, errors);
        if (errors.Count > 0) return new CompleteResult(CompleteOutcome.Invalid, Errors: errors.ToDictionary());

        var previousBest = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(score) FROM scores WHERE player_id = @PlayerId AND mode = @Mode;",
            new { session.PlayerId, rules.Mode }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE game_sessions
            SET status = 'completed', completed_at = now(), final_score = @Score, highest_level = @HighestLevel,
                levels_completed = @LevelsCompleted, lives_lost = @LivesLost, duration_seconds = @DurationSeconds
            WHERE id = @Id AND status = 'active';
            """,
            new { Id = sessionId, r.Score, r.HighestLevel, r.LevelsCompleted, r.LivesLost, r.DurationSeconds }, tx, cancellationToken: ct));

        var scoreId = Guid.CreateVersion7();
        var createdAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            """
            INSERT INTO scores (id, player_id, mode, score, level, duration_seconds, challenge_date, session_id, created_at)
            VALUES (@Id, @PlayerId, @Mode, @Score, @Level, @DurationSeconds, @ChallengeDate, @SessionId, now())
            RETURNING created_at;
            """,
            new
            {
                Id = scoreId,
                session.PlayerId,
                rules.Mode,
                r.Score,
                Level = r.HighestLevel,
                r.DurationSeconds,
                session.ChallengeDate,
                SessionId = sessionId,
            },
            tx, cancellationToken: ct));

        var rank = await ScoreRanking.RankAsync(conn, tx, rules.Mode, scoreId, r.Score, createdAt, session.ChallengeDate, session.PlayerId, ct);
        await tx.CommitAsync(ct);
        return new CompleteResult(CompleteOutcome.Completed, new CompleteSessionResponse(rank, previousBest is null || r.Score > previousBest, previousBest));
    }

    /// <summary>Recomputes the original answer for a session that was already completed.</summary>
    private static async Task<CompleteSessionResponse> ReplayResultAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string mode, SessionRow session, CancellationToken ct)
    {
        var score = await conn.QuerySingleAsync<(Guid Id, int Score, DateTime CreatedAt)>(new CommandDefinition(
            "SELECT id, score, created_at FROM scores WHERE session_id = @SessionId;",
            new { SessionId = session.Id }, tx, cancellationToken: ct));
        var previousBest = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(score) FROM scores WHERE player_id = @PlayerId AND mode = @Mode AND created_at < @CreatedAt;",
            new { session.PlayerId, Mode = mode, score.CreatedAt }, tx, cancellationToken: ct));
        var rank = await ScoreRanking.RankAsync(conn, tx, mode, score.Id, score.Score, score.CreatedAt, session.ChallengeDate, session.PlayerId, ct);
        return new CompleteSessionResponse(rank, previousBest is null || score.Score > previousBest, previousBest);
    }

    public async Task<PlayerGameStats> GetStatsAsync(string mode, string nickname, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<PlayerGameStats>(new CommandDefinition(
            """
            SELECT COUNT(*)::int AS games_played,
                   COALESCE(MAX(s.final_score), 0) AS highest_score,
                   COALESCE(MAX(s.highest_level), 0) AS highest_level,
                   COALESCE(SUM(s.final_score), 0)::bigint AS total_score,
                   MAX(s.completed_at) AS last_played_at
            FROM game_sessions s
            JOIN players p ON p.id = s.player_id
            WHERE p.nickname_normalized = @Key AND s.mode = @Mode AND s.status = 'completed';
            """,
            new { Key = ScoreRules.NicknameKey(ScoreRules.NormalizeNickname(nickname)), Mode = mode }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<GameHistoryEntry>> GetHistoryAsync(string mode, string nickname, int limit, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<GameHistoryEntry>(new CommandDefinition(
            """
            SELECT s.id AS session_id, s.final_score AS score, s.highest_level, s.levels_completed, s.lives_lost,
                   s.duration_seconds, to_char(s.challenge_date, 'YYYY-MM-DD') AS challenge_date, s.completed_at
            FROM game_sessions s
            JOIN players p ON p.id = s.player_id
            WHERE p.nickname_normalized = @Key AND s.mode = @Mode AND s.status = 'completed'
            ORDER BY s.completed_at DESC, s.id DESC
            LIMIT @Limit;
            """,
            new { Key = ScoreRules.NicknameKey(ScoreRules.NormalizeNickname(nickname)), Mode = mode, Limit = limit }, cancellationToken: ct));
        return rows.AsList();
    }
}
