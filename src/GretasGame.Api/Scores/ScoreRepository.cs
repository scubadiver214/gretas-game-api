using Dapper;
using Npgsql;

namespace GretasGame.Api.Scores;

public interface IScoreRepository
{
    Task<SubmitScoreResponse> SubmitAsync(string nickname, string character, string mode, int score, int durationSeconds, CancellationToken ct);
    Task<IReadOnlyList<ScoreEntry>> GetTopAsync(string mode, int limit, CancellationToken ct);
    Task<IReadOnlyList<PersonalBest>> GetPersonalBestsAsync(string nickname, CancellationToken ct);
}

public sealed class ScoreRepository(NpgsqlDataSource dataSource) : IScoreRepository
{
    public async Task<SubmitScoreResponse> SubmitAsync(
        string nickname, string character, string mode, int score, int durationSeconds, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var playerId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO players (id, nickname, nickname_normalized, character, created_at)
            VALUES (@Id, @Nickname, @Key, @Character, now())
            ON CONFLICT (nickname_normalized)
            DO UPDATE SET nickname = EXCLUDED.nickname, character = EXCLUDED.character
            RETURNING id;
            """,
            new { Id = Guid.CreateVersion7(), Nickname = nickname, Key = ScoreRules.NicknameKey(nickname), Character = character },
            tx, cancellationToken: ct));

        var previousBest = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(score) FROM scores WHERE player_id = @PlayerId AND mode = @Mode;",
            new { PlayerId = playerId, Mode = mode }, tx, cancellationToken: ct));

        var scoreId = Guid.CreateVersion7();
        var createdAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            """
            INSERT INTO scores (id, player_id, mode, score, duration_seconds, created_at)
            VALUES (@Id, @PlayerId, @Mode, @Score, @DurationSeconds, now())
            RETURNING created_at;
            """,
            new { Id = scoreId, PlayerId = playerId, Mode = mode, Score = score, DurationSeconds = durationSeconds },
            tx, cancellationToken: ct));

        var rank = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*) + 1
            FROM scores
            WHERE mode = @Mode
              AND (score > @Score OR (score = @Score AND created_at < @CreatedAt));
            """,
            new { Mode = mode, Score = score, CreatedAt = createdAt }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

        return new SubmitScoreResponse(scoreId, rank, previousBest is null || score > previousBest);
    }

    public async Task<IReadOnlyList<ScoreEntry>> GetTopAsync(string mode, int limit, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ScoreEntry>(new CommandDefinition(
            """
            SELECT ROW_NUMBER() OVER (ORDER BY s.score DESC, s.created_at ASC) AS rank,
                   p.nickname,
                   p.character,
                   s.score,
                   s.duration_seconds,
                   s.created_at
            FROM scores s
            JOIN players p ON p.id = s.player_id
            WHERE s.mode = @Mode
            ORDER BY s.score DESC, s.created_at ASC
            LIMIT @Limit;
            """,
            new { Mode = mode, Limit = limit }, cancellationToken: ct));
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
