using Microsoft.AspNetCore.Http.HttpResults;

namespace GretasGame.Api.Scores;

public static class ScoreEndpoints
{
    public static IEndpointRouteBuilder MapScoreEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Scores");

        group.MapPost("/scores", SubmitAsync)
            .WithName("SubmitScore")
            .WithSummary("Record a finished round and return its leaderboard rank.");

        group.MapGet("/scores/top", GetTopAsync)
            .WithName("GetTopScores")
            .WithSummary("Top scores for a game mode.");

        group.MapGet("/players/{nickname}/best", GetPersonalBestsAsync)
            .WithName("GetPersonalBests")
            .WithSummary("A player's best score per game mode.");

        return app;
    }

    private static async Task<Results<Created<SubmitScoreResponse>, ValidationProblem>> SubmitAsync(
        SubmitScoreRequest request, IScoreRepository repo, CancellationToken ct)
    {
        var errors = ScoreRules.Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var nickname = ScoreRules.NormalizeNickname(request.Nickname);
        var result = await repo.SubmitAsync(nickname, request.Character, request.Mode, request.Score, request.DurationSeconds, ct);
        return TypedResults.Created($"/api/scores/{result.Id}", result);
    }

    private static async Task<Results<Ok<IReadOnlyList<ScoreEntry>>, ValidationProblem>> GetTopAsync(
        string mode, int? limit, IScoreRepository repo, CancellationToken ct)
    {
        if (!ScoreRules.Modes.Contains(mode))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["mode"] = [$"Mode must be one of: {string.Join(", ", ScoreRules.Modes)}."],
            });
        }

        var rows = await repo.GetTopAsync(mode, ScoreRules.ClampLimit(limit), ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<Results<Ok<IReadOnlyList<PersonalBest>>, ValidationProblem>> GetPersonalBestsAsync(
        string nickname, IScoreRepository repo, CancellationToken ct)
    {
        var normalized = ScoreRules.NormalizeNickname(nickname);
        if (normalized.Length < ScoreRules.NicknameMin || normalized.Length > ScoreRules.NicknameMax)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["nickname"] = [$"Nickname must be {ScoreRules.NicknameMin}-{ScoreRules.NicknameMax} characters."],
            });
        }

        var rows = await repo.GetPersonalBestsAsync(normalized, ct);
        return TypedResults.Ok(rows);
    }
}
