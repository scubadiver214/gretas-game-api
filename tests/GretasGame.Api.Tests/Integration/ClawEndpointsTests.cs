using System.Net;
using System.Net.Http.Json;
using GretasGame.Api.Claw;
using GretasGame.Api.Scores;

namespace GretasGame.Api.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class ClawEndpointsTests(PostgresFixture fx)
{
    private const string Base = "/api/claw";
    private static readonly ClawContent Content = ClawContent.Current;

    private static string Nick() => $"c{Guid.NewGuid():N}"[..12];

    private static async Task<StartAttemptResponse> StartAsync(HttpClient client, string nick, int level = 1, string? mystery = null)
    {
        var res = await client.PostAsJsonAsync($"{Base}/players/{nick}/attempts", new StartAttemptRequest("girl", "#3FA34D", level, mystery));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<StartAttemptResponse>())!;
    }

    private static Task<HttpResponseMessage> GrabAsync(HttpClient client, Guid id, string nick, int seq, string? prizeId, bool succeeded, int elapsed = 1) =>
        client.PostAsJsonAsync($"{Base}/attempts/{id}/grabs", new RecordGrabRequest(nick, seq, prizeId, succeeded, elapsed));

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid id, string nick, int duration = 2) =>
        client.PostAsJsonAsync($"{Base}/attempts/{id}/complete", new CompleteAttemptRequest(nick, duration));

    private static async Task<ClawProfile> ProfileAsync(HttpClient client, string nick)
    {
        var res = await client.GetAsync($"{Base}/players/{nick}/profile");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ClawProfile>())!;
    }

    /// <summary>Plays and clears the player's current level with one grab per prize needed.</summary>
    private static async Task<CompleteAttemptResponse> ClearLevelAsync(HttpClient client, string nick, int level)
    {
        var attempt = await StartAsync(client, nick, level);
        var goal = Content.LevelByNumber[level].Goal;
        var prize = Content.Prizes.First(p => !p.MysteryOnly && p.UnlockLevel <= level);
        for (var seq = 1; seq <= goal; seq++)
        {
            var res = await GrabAsync(client, attempt.AttemptId, nick, seq, prize.Id, true);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
        var done = await CompleteAsync(client, attempt.AttemptId, nick);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        return (await done.Content.ReadFromJsonAsync<CompleteAttemptResponse>())!;
    }

    [Fact]
    public async Task Unknown_players_get_an_empty_profile()
    {
        using var client = fx.CreateClient();
        var profile = await ProfileAsync(client, Nick());
        Assert.Equal(1, profile.CurrentLevel);
        Assert.Null(profile.SelectedAvatar);
        Assert.Empty(profile.Prizes);
        Assert.Empty(profile.Avatars);
    }

    [Fact]
    public async Task Start_validates_the_player_and_the_level()
    {
        using var client = fx.CreateClient();
        var bad = await client.PostAsJsonAsync($"{Base}/players/x/attempts", new StartAttemptRequest("robot", null, 1));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var locked = await client.PostAsJsonAsync($"{Base}/players/{Nick()}/attempts", new StartAttemptRequest("girl", null, 2));
        Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
    }

    [Fact]
    public async Task A_won_prize_lands_in_the_collection_with_points_and_the_first_avatar()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var attempt = await StartAsync(client, nick);
        Assert.Equal(1, attempt.Level);
        Assert.Equal("level", attempt.Kind);

        var res = await GrabAsync(client, attempt.AttemptId, nick, 1, "teddy-brown", true);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<RecordGrabResponse>())!;
        Assert.True(body.Succeeded);
        Assert.Equal(1, body.Count);
        Assert.Equal(10, body.Points);
        Assert.Equal(10, body.TotalPoints);
        Assert.Equal(["baby-bear"], body.NewAvatars);

        var profile = await ProfileAsync(client, nick);
        Assert.Single(profile.Prizes);
        Assert.Equal("teddy-brown", profile.Prizes[0].PrizeId);
        Assert.Equal("baby-bear", profile.SelectedAvatar);
        Assert.Equal(1, profile.SuccessfulGrabs);
        Assert.Equal(1, profile.TotalGrabs);
    }

    [Fact]
    public async Task Misses_count_as_grabs_but_award_nothing()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var attempt = await StartAsync(client, nick);
        var res = await GrabAsync(client, attempt.AttemptId, nick, 1, null, false);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<RecordGrabResponse>())!;
        Assert.False(body.Succeeded);
        Assert.Equal(0, body.Points);
        Assert.Empty(body.NewAvatars);
        var profile = await ProfileAsync(client, nick);
        Assert.Empty(profile.Prizes);
        Assert.Equal(1, profile.TotalGrabs);
        Assert.Equal(0, profile.SuccessfulGrabs);

        // Completing without meeting the goal does not clear the level.
        var done = await CompleteAsync(client, attempt.AttemptId, nick);
        var summary = (await done.Content.ReadFromJsonAsync<CompleteAttemptResponse>())!;
        Assert.False(summary.Cleared);
        Assert.Equal(0, summary.Stars);
        Assert.Equal(1, summary.CurrentLevel);
    }

    [Fact]
    public async Task Repeating_a_grab_is_idempotent_and_a_different_result_for_the_same_seq_is_a_conflict()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var attempt = await StartAsync(client, nick);
        var first = await GrabAsync(client, attempt.AttemptId, nick, 1, "duck-yellow", true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var again = await GrabAsync(client, attempt.AttemptId, nick, 1, "duck-yellow", true);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = (await again.Content.ReadFromJsonAsync<RecordGrabResponse>())!;
        Assert.Equal(1, body.Count);
        Assert.Equal(1, await fx.CountAsync("SELECT COUNT(*) FROM claw_grabs WHERE attempt_id = @id", new { id = attempt.AttemptId }));
        Assert.Equal(1, await fx.CountAsync("SELECT count FROM claw_prizes c JOIN players p ON p.id = c.player_id WHERE p.nickname = @nick AND prize_id = 'duck-yellow'", new { nick }));

        var different = await GrabAsync(client, attempt.AttemptId, nick, 1, "teddy-brown", true);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        var skipped = await GrabAsync(client, attempt.AttemptId, nick, 3, null, false);
        Assert.Equal(HttpStatusCode.BadRequest, skipped.StatusCode);
    }

    [Fact]
    public async Task Someone_else_cannot_record_grabs_on_your_attempt()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var attempt = await StartAsync(client, nick);
        var res = await GrabAsync(client, attempt.AttemptId, Nick(), 1, "teddy-brown", true);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var done = await CompleteAsync(client, attempt.AttemptId, Nick());
        Assert.Equal(HttpStatusCode.Forbidden, done.StatusCode);
        var missing = await GrabAsync(client, Guid.NewGuid(), nick, 1, null, false);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Locked_prizes_are_rejected()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var attempt = await StartAsync(client, nick);
        var rare = Content.Prizes.First(p => p.UnlockLevel > 20 && !p.MysteryOnly);
        var res = await GrabAsync(client, attempt.AttemptId, nick, 1, rare.Id, true);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Clearing_a_level_pays_stars_unlocks_the_next_level_and_remembers_the_best()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var done = await ClearLevelAsync(client, nick, 1);
        Assert.True(done.Cleared);
        Assert.Equal(3, done.Stars);
        Assert.Equal(2, done.CurrentLevel);
        Assert.Null(done.MysteryUnlocked);

        var profile = await ProfileAsync(client, nick);
        Assert.Equal(2, profile.CurrentLevel);
        var level = Assert.Single(profile.Levels);
        Assert.Equal(1, level.Level);
        Assert.Equal(3, level.Stars);
        Assert.Equal(1, level.BestGrabs);

        // Replaying an earlier level keeps the best stars and does not move the current level.
        var replay = await StartAsync(client, nick, 1);
        await GrabAsync(client, replay.AttemptId, nick, 1, null, false);
        await GrabAsync(client, replay.AttemptId, nick, 2, null, false);
        await GrabAsync(client, replay.AttemptId, nick, 3, "teddy-brown", true);
        var replayDone = (await (await CompleteAsync(client, replay.AttemptId, nick)).Content.ReadFromJsonAsync<CompleteAttemptResponse>())!;
        Assert.True(replayDone.Cleared);
        Assert.Equal(2, replayDone.Stars);
        Assert.Equal(2, replayDone.CurrentLevel);
        var after = await ProfileAsync(client, nick);
        Assert.Equal(3, after.Levels[0].Stars);
        Assert.Equal(2, after.Levels[0].Clears);
    }

    [Fact]
    public async Task Completing_twice_returns_the_same_answer()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var attempt = await StartAsync(client, nick);
        await GrabAsync(client, attempt.AttemptId, nick, 1, "teddy-brown", true);
        var first = await (await CompleteAsync(client, attempt.AttemptId, nick, 2)).Content.ReadFromJsonAsync<CompleteAttemptResponse>();
        var second = await CompleteAsync(client, attempt.AttemptId, nick, 2);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<CompleteAttemptResponse>();
        Assert.Equal(first!.Stars, body!.Stars);
        Assert.Equal(first.CurrentLevel, body.CurrentLevel);
        Assert.Equal(first.Score, body.Score);
        Assert.Equal(first.Rank, body.Rank);
        Assert.Equal(1, await fx.CountAsync("SELECT COUNT(*) FROM claw_levels l JOIN players p ON p.id = l.player_id WHERE p.nickname = @nick", new { nick }));
        Assert.Equal(1, await fx.CountAsync("SELECT COUNT(*) FROM scores WHERE id = @id AND mode = 'claw'", new { id = attempt.AttemptId }));
        var conflicting = await CompleteAsync(client, attempt.AttemptId, nick, 9);
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        var late = await GrabAsync(client, attempt.AttemptId, nick, 2, null, false);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task Completing_a_level_posts_the_points_to_the_claw_leaderboard()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var done = await ClearLevelAsync(client, nick, 1);
        Assert.True(done.Score > 0);
        Assert.True(done.Rank >= 1);
        Assert.True(done.IsPersonalBest);

        var top = await client.GetFromJsonAsync<List<ScoreEntry>>("/api/scores/top?mode=claw&limit=100");
        var mine = Assert.Single(top!, row => row.Nickname == nick);
        Assert.Equal(done.Score, mine.Score);
        Assert.Equal(done.Rank, mine.Rank);
        Assert.Equal(1, mine.Level);
    }

    [Fact]
    public async Task Avatars_unlock_at_their_milestones_and_can_be_selected_only_once_unlocked()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var newAvatars = new List<string>();
        for (var level = 1; level <= 5; level++) newAvatars.AddRange((await ClearLevelAsync(client, nick, level)).NewAvatars);
        Assert.Contains("rainbow-unicorn", newAvatars);

        var profile = await ProfileAsync(client, nick);
        Assert.Contains(profile.Avatars, a => a.AvatarId == "baby-bear");
        Assert.Contains(profile.Avatars, a => a.AvatarId == "rainbow-unicorn");
        Assert.Equal("baby-bear", profile.SelectedAvatar);
        Assert.Equal(6, profile.CurrentLevel);

        var pick = await client.PutAsJsonAsync($"{Base}/players/{nick}/avatar", new SelectAvatarRequest("girl", null, "rainbow-unicorn"));
        Assert.Equal(HttpStatusCode.OK, pick.StatusCode);
        Assert.Equal("rainbow-unicorn", (await pick.Content.ReadFromJsonAsync<ClawProfile>())!.SelectedAvatar);

        var locked = await client.PutAsJsonAsync($"{Base}/players/{nick}/avatar", new SelectAvatarRequest("girl", null, "claw-champion"));
        Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
        var unknown = await client.PutAsJsonAsync($"{Base}/players/{nick}/avatar", new SelectAvatarRequest("girl", null, "nope"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Mystery_machines_open_after_a_world_and_award_their_prize_and_avatar()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var toybox = Content.WorldById["toybox"];
        var tooEarly = await client.PostAsJsonAsync($"{Base}/players/{nick}/attempts", new StartAttemptRequest("girl", null, 0, "toybox"));
        Assert.Equal(HttpStatusCode.BadRequest, tooEarly.StatusCode);

        CompleteAttemptResponse? last = null;
        for (var level = toybox.From; level <= toybox.To; level++) last = await ClearLevelAsync(client, nick, level);
        Assert.Equal("toybox", last!.MysteryUnlocked);

        var mystery = await StartAsync(client, nick, 0, "toybox");
        Assert.Equal("mystery", mystery.Kind);
        Assert.Equal(toybox.To, mystery.Level);
        var wrong = await GrabAsync(client, mystery.AttemptId, nick, 1, "teddy-brown", true);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var won = await GrabAsync(client, mystery.AttemptId, nick, 1, toybox.MysteryPrize, true);
        Assert.Equal(HttpStatusCode.OK, won.StatusCode);
        var body = (await won.Content.ReadFromJsonAsync<RecordGrabResponse>())!;
        Assert.Contains(toybox.Avatar, body.NewAvatars);
        var done = (await (await CompleteAsync(client, mystery.AttemptId, nick)).Content.ReadFromJsonAsync<CompleteAttemptResponse>())!;
        Assert.True(done.Cleared);
        Assert.Equal(toybox.To + 1, done.CurrentLevel);

        var profile = await ProfileAsync(client, nick);
        Assert.Equal(["toybox"], profile.MysteryWorldsWon);
        Assert.Contains(profile.Prizes, p => p.PrizeId == toybox.MysteryPrize);
    }

    [Fact]
    public async Task Favorites_toggle_on_owned_prizes_only()
    {
        using var client = fx.CreateClient();
        var nick = Nick();
        var attempt = await StartAsync(client, nick);
        await GrabAsync(client, attempt.AttemptId, nick, 1, "duck-yellow", true);
        var ok = await client.PutAsJsonAsync($"{Base}/players/{nick}/prizes/duck-yellow/favorite", new FavoriteRequest(true));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.True((await ProfileAsync(client, nick)).Prizes.Single().Favorite);
        var missing = await client.PutAsJsonAsync($"{Base}/players/{nick}/prizes/teddy-brown/favorite", new FavoriteRequest(true));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
