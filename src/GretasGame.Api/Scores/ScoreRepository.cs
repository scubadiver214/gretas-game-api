using Dapper;
using GretasGame.Api.Players;
using Npgsql;

namespace GretasGame.Api.Scores;

public sealed record NewScore(
    string Nickname,
    string Character,
    string? Color,
    string Mode,
    int Score,
    int Level,
    int DurationSeconds,
    DateOnly? ChallengeDate);

public interface IScoreRepository
{
    Task<SubmitScoreResponse> SubmitAsync(NewScore score, CancellationToken ct);
    /// <summary>All-time top scores, or - when <paramref name="challengeDate"/> is set - that day's board (best per player).</summary>
    Task<IReadOnlyList<ScoreEntry>> GetTopAsync(string mode, int limit, DateOnly? challengeDate, CancellationToken ct);
    Task<IReadOnlyList<PersonalBest>> GetPersonalBestsAsync(string nickname, CancellationToken ct);
}

/// <summary>
/// Leaderboard ordering shared by every board: higher score first, then
/// whoever got there first, then the (time-ordered v7) id so ranks never tie.
/// </summary>
public static class ScoreRanking
{
    /// <summary>1-based rank of a just-inserted score on its board.</summary>
    public static Task<long> RankAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string mode, Guid scoreId, int score, DateTime createdAt,
        DateOnly? challengeDate, Guid playerId, CancellationToken ct) =>
        challengeDate is null
            ? conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                SELECT COUNT(*) + 1
                FROM scores
                WHERE mode = @Mode AND challenge_date IS NULL
                  AND (score > @Score
                       OR (score = @Score AND created_at < @CreatedAt)
                       OR (score = @Score AND created_at = @CreatedAt AND id < @Id));
                """,
                new { Mode = mode, Score = score, CreatedAt = createdAt, Id = scoreId }, tx, cancellationToken: ct))
            // Daily boards count one entry per player (their best), matching GetTopAsync.
            : conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                SELECT COUNT(*) + 1
                FROM (
                    SELECT DISTINCT ON (player_id) id, score, created_at
                    FROM scores
                    WHERE mode = @Mode AND challenge_date = @ChallengeDate AND player_id <> @PlayerId
                    ORDER BY player_id, score DESC, created_at ASC, id ASC
                ) others
                WHERE others.score > @Score
                   OR (others.score = @Score AND others.created_at < @CreatedAt)
                   OR (others.score = @Score AND others.created_at = @CreatedAt AND others.id < @Id);
                """,
                new { Mode = mode, Score = score, ChallengeDate = challengeDate, PlayerId = playerId, CreatedAt = createdAt, Id = scoreId },
                tx, cancellationToken: ct));
}

public sealed class ScoreRepository(NpgsqlDataSource dataSource) : IScoreRepository
{
    public async Task<SubmitScoreResponse> SubmitAsync(NewScore s, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var playerId = await PlayerSql.UpsertAsync(conn, tx, s.Nickname, s.Character, s.Color, ct);

        var previousBest = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(score) FROM scores WHERE player_id = @PlayerId AND mode = @Mode;",
            new { PlayerId = playerId, s.Mode }, tx, cancellationToken: ct));

        var scoreId = Guid.CreateVersion7();
        var createdAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            """
            INSERT INTO scores (id, player_id, mode, score, level, duration_seconds, challenge_date, created_at)
            VALUES (@Id, @PlayerId, @Mode, @Score, @Level, @DurationSeconds, @ChallengeDate, now())
            RETURNING created_at;
            """,
            new { Id = scoreId, PlayerId = playerId, s.Mode, s.Score, s.Level, s.DurationSeconds, s.ChallengeDate },
            tx, cancellationToken: ct));

        var rank = await ScoreRanking.RankAsync(conn, tx, s.Mode, scoreId, s.Score, createdAt, s.ChallengeDate, playerId, ct);

        await tx.CommitAsync(ct);

        return new SubmitScoreResponse(scoreId, rank, previousBest is null || s.Score > previousBest);
    }

    public async Task<IReadOnlyList<ScoreEntry>> GetTopAsync(string mode, int limit, DateOnly? challengeDate, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var sql = challengeDate is null
            ? """
              SELECT ROW_NUMBER() OVER (ORDER BY s.score DESC, s.created_at ASC, s.id ASC) AS rank,
                     p.nickname, p.character, p.color,
                     s.score, s.level, s.duration_seconds, s.created_at
              FROM scores s
              JOIN players p ON p.id = s.player_id
              WHERE s.mode = @Mode AND s.challenge_date IS NULL
              ORDER BY s.score DESC, s.created_at ASC, s.id ASC
              LIMIT @Limit;
              """
            : """
              WITH best AS (
                  SELECT DISTINCT ON (s.player_id) s.id, s.player_id, s.score, s.level, s.duration_seconds, s.created_at
                  FROM scores s
                  WHERE s.mode = @Mode AND s.challenge_date = @ChallengeDate
                  ORDER BY s.player_id, s.score DESC, s.created_at ASC, s.id ASC
              )
              SELECT ROW_NUMBER() OVER (ORDER BY b.score DESC, b.created_at ASC, b.id ASC) AS rank,
                     p.nickname, p.character, p.color,
                     b.score, b.level, b.duration_seconds, b.created_at
              FROM best b
              JOIN players p ON p.id = b.player_id
              ORDER BY b.score DESC, b.created_at ASC, b.id ASC
              LIMIT @Limit;
              """;

        var rows = await conn.QueryAsync<ScoreEntry>(new CommandDefinition(
            sql, new { Mode = mode, Limit = limit, ChallengeDate = challengeDate }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PersonalBest>> GetPersonalBestsAsync(string nickname, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PersonalBest>(new CommandDefinition(
            """
            SELECT DISTINCT ON (s.mode) s.mode, s.score, s.created_at
            FROM scores s
            JOIN players p ON p.id = s.player_id
            WHERE p.nickname_normalized = @Key
            ORDER BY s.mode, s.score DESC, s.created_at ASC;
            """,
            new { Key = ScoreRules.NicknameKey(ScoreRules.NormalizeNickname(nickname)) }, cancellationToken: ct));
        return rows.AsList();
    }
}
