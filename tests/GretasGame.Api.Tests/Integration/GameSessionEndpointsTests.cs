using System.Net;
using System.Net.Http.Json;
using GretasGame.Api.Games;
using GretasGame.Api.Scores;

namespace GretasGame.Api.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class GameSessionEndpointsTests(PostgresFixture fx)
{
    private const string Base = "/api/games/frogger";

    /// <summary>Unique, valid nickname per test so tests never see each other's players.</summary>
    private static string Nick() => $"t{Guid.NewGuid():N}"[..12];

    private static CompleteSessionRequest Result(string nickname, int score, int durationSeconds = 2) =>
        new(nickname, score, HighestLevel: 1, LevelsCompleted: 0, LivesLost: 3, durationSeconds);

    private static async Task<Guid> StartAsync(HttpClient client, string nickname, string? challengeDate = null)
    {
        var res = await client.PostAsJsonAsync($"{Base}/sessions", new StartSessionRequest(nickname, "girl", "#3FA34D", challengeDate));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<StartSessionResponse>();
        return body!.SessionId;
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid id, CompleteSessionRequest body) =>
        client.PostAsJsonAsync($"{Base}/sessions/{id}/complete", body);

    /// <summary>Plays and finishes a game, asserting it was accepted.</summary>
    private static async Task<CompleteSessionResponse> PlayAsync(HttpClient client, string nickname, int score)
    {
        var id = await StartAsync(client, nickname);
        var res = await CompleteAsync(client, id, Result(nickname, score));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<CompleteSessionResponse>())!;
    }

    [Fact]
    public async Task Start_creates_an_active_session_for_the_player()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var id = await StartAsync(client, nick);
        Assert.Equal(1, await fx.CountAsync(
            "SELECT COUNT(*) FROM game_sessions s JOIN players p ON p.id = s.player_id WHERE s.id = @id AND s.status = 'active' AND p.nickname = @nick",
            new { id, nick }));
    }

    [Fact]
    public async Task Start_validates_the_player()
    {
        using var client = fx.CreateClient();
        var res = await client.PostAsJsonAsync($"{Base}/sessions", new StartSessionRequest("x", "robot"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_games_are_404()
    {
        using var client = fx.CreateClient();
        var res = await client.PostAsJsonAsync("/api/games/kitchen/sessions", new StartSessionRequest(Nick(), "girl"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Complete_persists_the_session_and_a_linked_score()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var id = await StartAsync(client, nick);
        var res = await CompleteAsync(client, id, Result(nick, 320));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<CompleteSessionResponse>();
        Assert.True(body!.IsPersonalBest);
        Assert.Null(body.PreviousBest);
        Assert.True(body.Rank >= 1);

        Assert.Equal(1, await fx.CountAsync(
            "SELECT COUNT(*) FROM game_sessions WHERE id = @id AND status = 'completed' AND final_score = 320 AND completed_at IS NOT NULL",
            new { id }));
        Assert.Equal(1, await fx.CountAsync("SELECT COUNT(*) FROM scores WHERE session_id = @id AND mode = 'frogger' AND score = 320 AND level = 1", new { id }));
    }

    [Fact]
    public async Task Identical_retries_return_the_same_result_and_never_duplicate_the_score()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var id = await StartAsync(client, nick);
        var first = await (await CompleteAsync(client, id, Result(nick, 200))).Content.ReadFromJsonAsync<CompleteSessionResponse>();

        var retry = await CompleteAsync(client, id, Result(nick, 200));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var again = await retry.Content.ReadFromJsonAsync<CompleteSessionResponse>();
        Assert.Equal(first, again);

        var changed = await CompleteAsync(client, id, Result(nick, 210));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(1, await fx.CountAsync("SELECT COUNT(*) FROM scores WHERE session_id = @id", new { id }));
    }

    [Fact]
    public async Task Only_the_owner_can_complete_a_session()
    {
        using var client = fx.CreateClient();
        var id = await StartAsync(client, Nick());
        var res = await CompleteAsync(client, id, Result(Nick(), 100));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_sessions_are_404()
    {
        using var client = fx.CreateClient();
        var res = await CompleteAsync(client, Guid.NewGuid(), Result(Nick(), 100));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Expired_sessions_cannot_be_completed()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var id = await StartAsync(client, nick);
        await fx.ExecuteAsync("UPDATE game_sessions SET started_at = now() - interval '5 hours' WHERE id = @id", new { id });
        var res = await CompleteAsync(client, id, Result(nick, 100));
        Assert.Equal(HttpStatusCode.Gone, res.StatusCode);
    }

    [Theory]
    [InlineData(999_990, 0, 1)] // far above what level 1 allows
    [InlineData(105, 0, 1)]     // not a multiple of 10
    [InlineData(100, 2, 1)]     // highest level doesn't match levels completed
    public async Task Implausible_results_are_rejected(int score, int levelsCompleted, int highestLevel)
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var id = await StartAsync(client, nick);
        var res = await CompleteAsync(client, id, Result(nick, score) with { LevelsCompleted = levelsCompleted, HighestLevel = highestLevel });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(0, await fx.CountAsync("SELECT COUNT(*) FROM scores WHERE session_id = @id", new { id }));
    }

    [Fact]
    public async Task Levels_need_real_time_on_the_server_clock()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var id = await StartAsync(client, nick);
        var result = new CompleteSessionRequest(nick, 4_000, HighestLevel: 2, LevelsCompleted: 1, LivesLost: 3, DurationSeconds: 3);
        Assert.Equal(HttpStatusCode.BadRequest, (await CompleteAsync(client, id, result)).StatusCode);

        // Same result after the session has really been open long enough.
        await fx.ExecuteAsync("UPDATE game_sessions SET started_at = now() - interval '2 minutes' WHERE id = @id", new { id });
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, id, result)).StatusCode);
    }

    [Fact]
    public async Task Leaderboard_orders_by_score_then_earliest_then_id()
    {
        using var client = fx.CreateClient();
        // Scores above anything the other tests post, so these three are the top of the board.
        var (a, b, c) = (Nick(), Nick(), Nick());
        await PlayAsync(client, a, 3_000);
        await PlayAsync(client, b, 3_400);
        await PlayAsync(client, c, 3_000);

        var top = await client.GetFromJsonAsync<List<ScoreEntry>>("/api/scores/top?mode=frogger&limit=100");
        var ours = top!.Where(e => e.Nickname == a || e.Nickname == b || e.Nickname == c).ToList();
        Assert.Equal(new[] { b, a, c }, ours.Select(e => e.Nickname));
        Assert.True(ours[0].Rank < ours[1].Rank && ours[1].Rank < ours[2].Rank);
    }

    [Fact]
    public async Task Daily_sessions_land_on_that_days_board()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var id = await StartAsync(client, nick, today);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, id, Result(nick, 150))).StatusCode);

        var daily = await client.GetFromJsonAsync<List<ScoreEntry>>($"/api/scores/top?mode=frogger&limit=100&date={today}");
        Assert.Contains(daily!, e => e.Nickname == nick && e.Score == 150);
        var allTime = await client.GetFromJsonAsync<List<ScoreEntry>>("/api/scores/top?mode=frogger&limit=100");
        Assert.DoesNotContain(allTime!, e => e.Nickname == nick);
    }

    [Fact]
    public async Task Stats_history_and_personal_bests_aggregate_finished_games()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        await PlayAsync(client, nick, 100);
        var second = await PlayAsync(client, nick, 40);
        Assert.False(second.IsPersonalBest);
        Assert.Equal(100, second.PreviousBest);
        await StartAsync(client, nick); // unfinished games don't count

        var stats = await client.GetFromJsonAsync<PlayerGameStats>($"{Base}/players/{nick}/stats");
        Assert.Equal(2, stats!.GamesPlayed);
        Assert.Equal(100, stats.HighestScore);
        Assert.Equal(140, stats.TotalScore);
        Assert.Equal(1, stats.HighestLevel);
        Assert.NotNull(stats.LastPlayedAt);

        var history = await client.GetFromJsonAsync<List<GameHistoryEntry>>($"{Base}/players/{nick.ToUpperInvariant()}/history");
        Assert.Equal(new[] { 40, 100 }, history!.Select(h => h.Score));

        var bests = await client.GetFromJsonAsync<List<PersonalBest>>($"/api/players/{nick}/best");
        Assert.Contains(bests!, b => b.Mode == "frogger" && b.Score == 100);
    }

    [Fact]
    public async Task Stats_for_a_new_player_are_zero()
    {
        using var client = fx.CreateClient();
        var stats = await client.GetFromJsonAsync<PlayerGameStats>($"{Base}/players/{Nick()}/stats");
        Assert.Equal(0, stats!.GamesPlayed);
        Assert.Null(stats.LastPlayedAt);
    }

    [Fact]
    public async Task Session_writes_are_rate_limited_per_client()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 35 && last != HttpStatusCode.TooManyRequests; i++)
        {
            last = (await client.PostAsJsonAsync($"{Base}/sessions", new StartSessionRequest(nick, "girl"))).StatusCode;
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public async Task Direct_score_submits_still_work_for_other_games_but_not_frogger()
    {
        using var client = fx.CreateClient();
        var kitchen = await client.PostAsJsonAsync("/api/scores", new SubmitScoreRequest(Nick(), "boy", "kitchen", 120, 45));
        Assert.Equal(HttpStatusCode.Created, kitchen.StatusCode);

        var frogger = await client.PostAsJsonAsync("/api/scores", new SubmitScoreRequest(Nick(), "boy", "frogger", 120, 45));
        Assert.Equal(HttpStatusCode.BadRequest, frogger.StatusCode);
    }
}
