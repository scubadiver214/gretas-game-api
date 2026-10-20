using Dapper;
using GretasGame.Api.Scores;
using Npgsql;

namespace GretasGame.Api.Players;

/// <summary>Player rows are created on first write and refreshed (character, colour) on every later one.</summary>
public static class PlayerSql
{
    /// <summary>Inserts or updates the player for <paramref name="nickname"/> (already normalised) and returns its id.</summary>
    public static Task<Guid> UpsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string nickname, string character, string? color, CancellationToken ct) =>
        conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO players (id, nickname, nickname_normalized, character, color, created_at)
            VALUES (@Id, @Nickname, @Key, @Character, @Color, now())
            ON CONFLICT (nickname_normalized)
            DO UPDATE SET nickname = EXCLUDED.nickname,
                          character = EXCLUDED.character,
                          color = COALESCE(EXCLUDED.color, players.color)
            RETURNING id;
            """,
            new { Id = Guid.CreateVersion7(), Nickname = nickname, Key = ScoreRules.NicknameKey(nickname), Character = character, Color = color },
            tx, cancellationToken: ct));
}
