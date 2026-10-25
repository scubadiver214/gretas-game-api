using Dapper;
using GretasGame.Api.Players;
using GretasGame.Api.Scores;
using Npgsql;

namespace GretasGame.Api.Claw;

public enum ClawOutcome
{
    Ok,
    Invalid,
    NotFound,
    Forbidden,
    Expired,
    Conflict,
}

public sealed record ClawResult<T>(ClawOutcome Outcome, T? Response = null, IReadOnlyDictionary<string, string[]>? Errors = null) where T : class
{
    public static ClawResult<T> Ok(T response) => new(ClawOutcome.Ok, response);
    public static ClawResult<T> Invalid(ValidationErrors errors) => new(ClawOutcome.Invalid, Errors: errors.ToDictionary());
    public static ClawResult<T> Fail(ClawOutcome outcome) => new(outcome);
}

public interface IClawRepository
{
    Task<ClawProfile> GetProfileAsync(string nickname, CancellationToken ct);
    Task<ClawResult<StartAttemptResponse>> StartAsync(string nickname, StartAttemptRequest request, CancellationToken ct);
    Task<ClawResult<RecordGrabResponse>> RecordGrabAsync(Guid attemptId, RecordGrabRequest request, CancellationToken ct);
    Task<ClawResult<CompleteAttemptResponse>> CompleteAsync(Guid attemptId, CompleteAttemptRequest request, CancellationToken ct);
    Task<ClawResult<ClawProfile>> SelectAvatarAsync(string nickname, SelectAvatarRequest request, CancellationToken ct);
    Task<bool> SetFavoriteAsync(string nickname, string prizeId, bool favorite, CancellationToken ct);
}

/// <summary>
/// All claw writes happen in one transaction per request with the attempt
/// row locked, so retries and concurrent duplicates see the finished state.
/// </summary>
public sealed class ClawRepository(NpgsqlDataSource dataSource, ClawRules rules) : IClawRepository
{
    private sealed record AttemptRow(
        Guid Id,
        Guid PlayerId,
        string NicknameNormalized,
        int Level,
        string Kind,
        string? World,
        string Status,
        DateTime StartedAt,
        int Grabs,
        int Successes,
        int? Stars,
        int? DurationSeconds,
        DateTime Now);

    private sealed record ProfileRow(Guid PlayerId, string Nickname, int CurrentLevel, string? SelectedAvatar, long Points, int TotalGrabs, int SuccessfulGrabs);

    private sealed record GrabRow(int ClientSeq, string? PrizeId, bool Succeeded);

    private static string Key(string nickname) => ScoreRules.NicknameKey(ScoreRules.NormalizeNickname(nickname));

    // ---------------------------------------------------------------------------

    public async Task<ClawProfile> GetProfileAsync(string nickname, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var profile = await conn.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
            """
            SELECT c.player_id, p.nickname, c.current_level, c.selected_avatar, c.points, c.total_grabs, c.successful_grabs
            FROM claw_profiles c JOIN players p ON p.id = c.player_id
            WHERE p.nickname_normalized = @Key;
            """,
            new { Key = Key(nickname) }, cancellationToken: ct));
        if (profile is null) return ClawProfile.Empty(ScoreRules.NormalizeNickname(nickname));
        return await LoadProfileAsync(conn, null, profile, ct);
    }

    private static async Task<ClawProfile> LoadProfileAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, ProfileRow profile, CancellationToken ct)
    {
        var args = new { profile.PlayerId };
        var prizes = (await conn.QueryAsync<ClawPrizeEntry>(new CommandDefinition(
            "SELECT prize_id, count, favorite, first_collected_at, last_collected_at FROM claw_prizes WHERE player_id = @PlayerId ORDER BY first_collected_at, prize_id;",
            args, tx, cancellationToken: ct))).AsList();
        var avatars = (await conn.QueryAsync<ClawAvatarEntry>(new CommandDefinition(
            "SELECT avatar_id, unlocked_at FROM claw_avatars WHERE player_id = @PlayerId ORDER BY unlocked_at, avatar_id;",
            args, tx, cancellationToken: ct))).AsList();
        var levels = (await conn.QueryAsync<ClawLevelEntry>(new CommandDefinition(
            "SELECT level, stars, best_grabs, clears, last_cleared_at FROM claw_levels WHERE player_id = @PlayerId ORDER BY level;",
            args, tx, cancellationToken: ct))).AsList();
        var mystery = (await conn.QueryAsync<string>(new CommandDefinition(MysteryWorldsSql, args, tx, cancellationToken: ct))).AsList();
        return new ClawProfile(profile.Nickname, profile.CurrentLevel, profile.SelectedAvatar, profile.Points, profile.TotalGrabs, profile.SuccessfulGrabs, prizes, avatars, levels, mystery);
    }

    private const string MysteryWorldsSql =
        "SELECT DISTINCT world FROM claw_attempts WHERE player_id = @PlayerId AND kind = 'mystery' AND successes >= 1 AND world IS NOT NULL ORDER BY world;";

    // ---------------------------------------------------------------------------

    public async Task<ClawResult<StartAttemptResponse>> StartAsync(string nickname, StartAttemptRequest r, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var playerId = await PlayerSql.UpsertAsync(conn, tx, ScoreRules.NormalizeNickname(nickname), r.Character, r.Color, ct);
        var currentLevel = await EnsureProfileAsync(conn, tx, playerId, ct);

        var errors = new ValidationErrors();
        rules.ValidateStart(r, currentLevel, errors);
        if (errors.Count > 0) return ClawResult<StartAttemptResponse>.Invalid(errors);

        var kind = r.Mystery is null ? "level" : "mystery";
        var level = r.Mystery is null ? r.Level : rules.Content.WorldById[r.Mystery].To;
        var id = Guid.CreateVersion7();
        var startedAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            """
            INSERT INTO claw_attempts (id, player_id, level, kind, world, status, started_at)
            VALUES (@Id, @PlayerId, @Level, @Kind, @World, 'active', now())
            RETURNING started_at;
            """,
            new { Id = id, PlayerId = playerId, Level = level, Kind = kind, World = r.Mystery }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return ClawResult<StartAttemptResponse>.Ok(new StartAttemptResponse(id, startedAt, level, kind));
    }

    /// <summary>Creates the profile row on first contact and returns the current level.</summary>
    private static async Task<int> EnsureProfileAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid playerId, CancellationToken ct)
    {
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            INSERT INTO claw_profiles (player_id) VALUES (@PlayerId)
            ON CONFLICT (player_id) DO UPDATE SET updated_at = claw_profiles.updated_at
            RETURNING current_level;
            """,
            new { PlayerId = playerId }, tx, cancellationToken: ct));
    }

    private static Task<AttemptRow?> LockAttemptAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<AttemptRow>(new CommandDefinition(
            """
            SELECT a.id, a.player_id, p.nickname_normalized, a.level, a.kind, a.world, a.status, a.started_at,
                   a.grabs, a.successes, a.stars, a.duration_seconds, now() AS now
            FROM claw_attempts a JOIN players p ON p.id = a.player_id
            WHERE a.id = @Id
            FOR UPDATE OF a;
            """,
            new { Id = id }, tx, cancellationToken: ct));

    // ---------------------------------------------------------------------------

    public async Task<ClawResult<RecordGrabResponse>> RecordGrabAsync(Guid attemptId, RecordGrabRequest r, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var attempt = await LockAttemptAsync(conn, tx, attemptId, ct);
        if (attempt is null) return ClawResult<RecordGrabResponse>.Fail(ClawOutcome.NotFound);
        if (!string.Equals(attempt.NicknameNormalized, Key(r.Nickname ?? string.Empty), StringComparison.Ordinal))
            return ClawResult<RecordGrabResponse>.Fail(ClawOutcome.Forbidden);

        // Replays: the same grab number again is answered from what was stored.
        var existing = await conn.QuerySingleOrDefaultAsync<GrabRow>(new CommandDefinition(
            "SELECT client_seq, prize_id, succeeded FROM claw_grabs WHERE attempt_id = @Id AND client_seq = @Seq;",
            new { Id = attemptId, r.Seq }, tx, cancellationToken: ct));
        if (existing is not null)
        {
            if (existing.Succeeded != r.Succeeded || !string.Equals(existing.PrizeId, r.Succeeded ? r.PrizeId : existing.PrizeId, StringComparison.Ordinal))
                return ClawResult<RecordGrabResponse>.Fail(ClawOutcome.Conflict);
            var replay = await GrabResponseAsync(conn, tx, attempt.PlayerId, existing.Succeeded, existing.PrizeId, [], ct);
            await tx.CommitAsync(ct);
            return ClawResult<RecordGrabResponse>.Ok(replay);
        }

        if (attempt.Status == "completed") return ClawResult<RecordGrabResponse>.Fail(ClawOutcome.Conflict);
        var elapsed = attempt.Now - attempt.StartedAt;
        if (elapsed > ClawRules.AttemptLifetime) return ClawResult<RecordGrabResponse>.Fail(ClawOutcome.Expired);

        var errors = new ValidationErrors();
        rules.ValidateGrab(r, attempt.Level, attempt.Kind, attempt.World, attempt.Grabs, elapsed, errors);
        if (errors.Count > 0) return ClawResult<RecordGrabResponse>.Invalid(errors);

        var prizeId = r.Succeeded ? r.PrizeId : null;
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO claw_grabs (id, attempt_id, client_seq, prize_id, succeeded, recorded_at) VALUES (@Id, @AttemptId, @Seq, @PrizeId, @Succeeded, now());",
            new { Id = Guid.CreateVersion7(), AttemptId = attemptId, r.Seq, PrizeId = prizeId, r.Succeeded }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE claw_attempts SET grabs = grabs + 1, successes = successes + @Won WHERE id = @Id;",
            new { Id = attemptId, Won = r.Succeeded ? 1 : 0 }, tx, cancellationToken: ct));

        var points = 0;
        if (r.Succeeded && prizeId is not null)
        {
            points = rules.Content.PrizeById[prizeId].Points;
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO claw_prizes (player_id, prize_id, count, first_collected_at, last_collected_at)
                VALUES (@PlayerId, @PrizeId, 1, now(), now())
                ON CONFLICT (player_id, prize_id) DO UPDATE SET count = claw_prizes.count + 1, last_collected_at = now();
                """,
                new { attempt.PlayerId, PrizeId = prizeId }, tx, cancellationToken: ct));
        }
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE claw_profiles
            SET points = points + @Points, total_grabs = total_grabs + 1, successful_grabs = successful_grabs + @Won, updated_at = now()
            WHERE player_id = @PlayerId;
            """,
            new { attempt.PlayerId, Points = points, Won = r.Succeeded ? 1 : 0 }, tx, cancellationToken: ct));

        var newAvatars = r.Succeeded ? await AwardMilestonesAsync(conn, tx, attempt.PlayerId, ct) : [];
        var response = await GrabResponseAsync(conn, tx, attempt.PlayerId, r.Succeeded, prizeId, newAvatars, ct);
        await tx.CommitAsync(ct);
        return ClawResult<RecordGrabResponse>.Ok(response);
    }

    private async Task<RecordGrabResponse> GrabResponseAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid playerId, bool succeeded, string? prizeId, IReadOnlyList<string> newAvatars, CancellationToken ct)
    {
        var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT points FROM claw_profiles WHERE player_id = @PlayerId;", new { PlayerId = playerId }, tx, cancellationToken: ct));
        var count = 0;
        var points = 0;
        if (succeeded && prizeId is not null)
        {
            count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count FROM claw_prizes WHERE player_id = @PlayerId AND prize_id = @PrizeId;", new { PlayerId = playerId, PrizeId = prizeId }, tx, cancellationToken: ct));
            points = rules.Content.PrizeById.TryGetValue(prizeId, out var p) ? p.Points : 0;
        }
        return new RecordGrabResponse(succeeded, prizeId, count, points, total, newAvatars);
    }

    // ---------------------------------------------------------------------------

    public async Task<ClawResult<CompleteAttemptResponse>> CompleteAsync(Guid attemptId, CompleteAttemptRequest r, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var attempt = await LockAttemptAsync(conn, tx, attemptId, ct);
        if (attempt is null) return ClawResult<CompleteAttemptResponse>.Fail(ClawOutcome.NotFound);
        if (!string.Equals(attempt.NicknameNormalized, Key(r.Nickname ?? string.Empty), StringComparison.Ordinal))
            return ClawResult<CompleteAttemptResponse>.Fail(ClawOutcome.Forbidden);

        var goal = attempt.Kind == "mystery" ? 1 : rules.Content.LevelByNumber[attempt.Level].Goal;
        if (attempt.Status == "completed")
        {
            if (attempt.DurationSeconds != r.DurationSeconds) return ClawResult<CompleteAttemptResponse>.Fail(ClawOutcome.Conflict);
            var level = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT current_level FROM claw_profiles WHERE player_id = @PlayerId;", new { attempt.PlayerId }, tx, cancellationToken: ct));
            var replay = await WriteClawScoreAsync(conn, tx, attempt, r.DurationSeconds, ct);
            await tx.CommitAsync(ct);
            return ClawResult<CompleteAttemptResponse>.Ok(new CompleteAttemptResponse(
                attempt.Stars ?? 0, attempt.Successes >= goal, level, [], null, replay.Score, replay.Rank, replay.IsPersonalBest));
        }

        var elapsed = attempt.Now - attempt.StartedAt;
        if (elapsed > ClawRules.AttemptLifetime) return ClawResult<CompleteAttemptResponse>.Fail(ClawOutcome.Expired);
        var errors = new ValidationErrors();
        rules.ValidateComplete(r, elapsed, errors);
        if (errors.Count > 0) return ClawResult<CompleteAttemptResponse>.Invalid(errors);

        var cleared = attempt.Successes >= goal;
        var stars = cleared ? rules.Stars(goal, attempt.Grabs) : 0;
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE claw_attempts SET status = 'completed', completed_at = now(), stars = @Stars, duration_seconds = @Duration WHERE id = @Id AND status = 'active';",
            new { Id = attemptId, Stars = stars, Duration = r.DurationSeconds }, tx, cancellationToken: ct));

        string? mysteryUnlocked = null;
        var currentLevel = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT current_level FROM claw_profiles WHERE player_id = @PlayerId;", new { attempt.PlayerId }, tx, cancellationToken: ct));
        if (cleared && attempt.Kind == "level")
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO claw_levels (player_id, level, stars, best_grabs, clears, first_cleared_at, last_cleared_at)
                VALUES (@PlayerId, @Level, @Stars, @Grabs, 1, now(), now())
                ON CONFLICT (player_id, level) DO UPDATE
                SET stars = GREATEST(claw_levels.stars, EXCLUDED.stars),
                    best_grabs = LEAST(claw_levels.best_grabs, EXCLUDED.best_grabs),
                    clears = claw_levels.clears + 1,
                    last_cleared_at = now();
                """,
                new { attempt.PlayerId, attempt.Level, Stars = stars, attempt.Grabs }, tx, cancellationToken: ct));
            if (attempt.Level == currentLevel)
            {
                var world = rules.Content.WorldForLevel(attempt.Level);
                if (world is not null && world.To == attempt.Level) mysteryUnlocked = world.Id;
                currentLevel = Math.Min(rules.Content.MaxLevel, attempt.Level + 1);
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE claw_profiles SET current_level = @Level, updated_at = now() WHERE player_id = @PlayerId;",
                    new { attempt.PlayerId, Level = currentLevel }, tx, cancellationToken: ct));
            }
        }

        var newAvatars = cleared ? await AwardMilestonesAsync(conn, tx, attempt.PlayerId, ct) : [];
        var board = await WriteClawScoreAsync(conn, tx, attempt, r.DurationSeconds, ct);
        await tx.CommitAsync(ct);
        return ClawResult<CompleteAttemptResponse>.Ok(new CompleteAttemptResponse(
            stars, cleared, currentLevel, newAvatars, mysteryUnlocked, board.Score, board.Rank, board.IsPersonalBest));
    }

    /// <summary>
    /// One <c>scores</c> row per attempt, keyed by the attempt id so retries never
    /// double-count. The points are the prizes already recorded on this attempt.
    /// </summary>
    private async Task<(int Score, long Rank, bool IsPersonalBest)> WriteClawScoreAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, AttemptRow attempt, int durationSeconds, CancellationToken ct)
    {
        const string mode = "claw";
        var existing = await conn.QuerySingleOrDefaultAsync<(Guid Id, int Score, DateTime CreatedAt)>(new CommandDefinition(
            "SELECT id, score, created_at FROM scores WHERE id = @Id;",
            new { attempt.Id }, tx, cancellationToken: ct));

        int score;
        Guid scoreId;
        DateTime createdAt;
        if (existing.Id != Guid.Empty)
        {
            score = existing.Score;
            scoreId = existing.Id;
            createdAt = existing.CreatedAt;
        }
        else
        {
            var prizes = await conn.QueryAsync<(string? PrizeId, bool Succeeded)>(new CommandDefinition(
                "SELECT prize_id, succeeded FROM claw_grabs WHERE attempt_id = @Id;",
                new { attempt.Id }, tx, cancellationToken: ct));
            score = 0;
            foreach (var grab in prizes)
            {
                if (!grab.Succeeded || string.IsNullOrEmpty(grab.PrizeId)) continue;
                if (rules.Content.PrizeById.TryGetValue(grab.PrizeId, out var prize)) score += prize.Points;
            }

            scoreId = attempt.Id;
            createdAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
                """
                INSERT INTO scores (id, player_id, mode, score, level, duration_seconds, challenge_date, created_at)
                VALUES (@Id, @PlayerId, @Mode, @Score, @Level, @Duration, NULL, now())
                RETURNING created_at;
                """,
                new
                {
                    Id = scoreId,
                    attempt.PlayerId,
                    Mode = mode,
                    Score = score,
                    attempt.Level,
                    Duration = Math.Max(1, durationSeconds),
                },
                tx, cancellationToken: ct));
        }

        var previousBest = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(score) FROM scores WHERE player_id = @PlayerId AND mode = @Mode AND id <> @Id;",
            new { attempt.PlayerId, Mode = mode, Id = scoreId }, tx, cancellationToken: ct));
        var rank = await ScoreRanking.RankAsync(conn, tx, mode, scoreId, score, createdAt, null, attempt.PlayerId, ct);
        return (score, rank, previousBest is null || score > previousBest);
    }

    // ---------------------------------------------------------------------------

    /// <summary>Evaluates every avatar rule against the player's tables and records the new unlocks.</summary>
    private async Task<IReadOnlyList<string>> AwardMilestonesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid playerId, CancellationToken ct)
    {
        var args = new { PlayerId = playerId };
        var stats = await conn.QuerySingleAsync<(int SuccessfulGrabs, long PrizesCollected, long UniquePrizes, int HighestLevelCleared)>(new CommandDefinition(
            """
            SELECT c.successful_grabs,
                   COALESCE((SELECT SUM(count) FROM claw_prizes WHERE player_id = c.player_id), 0)::bigint,
                   COALESCE((SELECT COUNT(*) FROM claw_prizes WHERE player_id = c.player_id), 0)::bigint,
                   COALESCE((SELECT MAX(level) FROM claw_levels WHERE player_id = c.player_id), 0)
            FROM claw_profiles c WHERE c.player_id = @PlayerId;
            """,
            args, tx, cancellationToken: ct));
        var mystery = (await conn.QueryAsync<string>(new CommandDefinition(MysteryWorldsSql, args, tx, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
        var unlocked = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT avatar_id FROM claw_avatars WHERE player_id = @PlayerId;", args, tx, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);

        var ctx = new MilestoneContext(stats.SuccessfulGrabs, (int)Math.Min(int.MaxValue, stats.PrizesCollected), (int)Math.Min(int.MaxValue, stats.UniquePrizes), stats.HighestLevelCleared, mystery);
        var fresh = rules.EvaluateMilestones(ctx, unlocked);
        if (fresh.Count == 0) return fresh;

        foreach (var id in fresh)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO claw_avatars (player_id, avatar_id, unlocked_at) VALUES (@PlayerId, @AvatarId, now()) ON CONFLICT DO NOTHING;",
                new { PlayerId = playerId, AvatarId = id }, tx, cancellationToken: ct));
        }
        // A player with no avatar yet wears the first one they earn.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE claw_profiles SET selected_avatar = @AvatarId, updated_at = now() WHERE player_id = @PlayerId AND selected_avatar IS NULL;",
            new { PlayerId = playerId, AvatarId = fresh[0] }, tx, cancellationToken: ct));
        return fresh;
    }

    // ---------------------------------------------------------------------------

    public async Task<ClawResult<ClawProfile>> SelectAvatarAsync(string nickname, SelectAvatarRequest r, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var playerId = await PlayerSql.UpsertAsync(conn, tx, ScoreRules.NormalizeNickname(nickname), r.Character, r.Color, ct);
        await EnsureProfileAsync(conn, tx, playerId, ct);
        var owned = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM claw_avatars WHERE player_id = @PlayerId AND avatar_id = @AvatarId;",
            new { PlayerId = playerId, r.AvatarId }, tx, cancellationToken: ct));
        if (owned == 0)
        {
            var errors = new ValidationErrors();
            errors.Add(nameof(r.AvatarId), rules.Content.AvatarById.ContainsKey(r.AvatarId ?? string.Empty) ? "That avatar is not unlocked yet." : "Unknown avatar.");
            return ClawResult<ClawProfile>.Invalid(errors);
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE claw_profiles SET selected_avatar = @AvatarId, updated_at = now() WHERE player_id = @PlayerId;",
            new { PlayerId = playerId, r.AvatarId }, tx, cancellationToken: ct));
        var profile = await conn.QuerySingleAsync<ProfileRow>(new CommandDefinition(
            """
            SELECT c.player_id, p.nickname, c.current_level, c.selected_avatar, c.points, c.total_grabs, c.successful_grabs
            FROM claw_profiles c JOIN players p ON p.id = c.player_id WHERE c.player_id = @PlayerId;
            """,
            new { PlayerId = playerId }, tx, cancellationToken: ct));
        var full = await LoadProfileAsync(conn, tx, profile, ct);
        await tx.CommitAsync(ct);
        return ClawResult<ClawProfile>.Ok(full);
    }

    public async Task<bool> SetFavoriteAsync(string nickname, string prizeId, bool favorite, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE claw_prizes c SET favorite = @Favorite
            FROM players p
            WHERE p.id = c.player_id AND p.nickname_normalized = @Key AND c.prize_id = @PrizeId;
            """,
            new { Favorite = favorite, Key = Key(nickname), PrizeId = prizeId }, cancellationToken: ct));
        return rows > 0;
    }
}
