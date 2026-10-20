using System.Threading.RateLimiting;
using GretasGame.Api.Scores;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;

namespace GretasGame.Api.Games;

public static class GameSessionEndpoints
{
    public const string RateLimitPolicy = "game-sessions";
    public const int DefaultHistoryLimit = 20;
    public const int MaxHistoryLimit = 100;

    public static IEndpointRouteBuilder MapGameSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/games/{game}").WithTags("Game sessions");

        group.MapPost("/sessions", StartAsync)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("StartGameSession")
            .WithSummary("Start a server-tracked game session for the player.");

        group.MapPost("/sessions/{id:guid}/complete", CompleteAsync)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("CompleteGameSession")
            .WithSummary("Submit a finished game's results; records the score and returns its rank. Safe to retry.");

        group.MapGet("/players/{nickname}/stats", GetStatsAsync)
            .WithName("GetPlayerGameStats")
            .WithSummary("Games played, best score and level, total score and last played for a player.");

        group.MapGet("/players/{nickname}/history", GetHistoryAsync)
            .WithName("GetPlayerGameHistory")
            .WithSummary("A player's most recent finished games, newest first.");

        return app;
    }

    /// <summary>
    /// Fixed window per client IP. Behind the Next.js proxy and Azure ingress the
    /// socket address is the proxy's, so the first X-Forwarded-For hop is preferred.
    /// </summary>
    public static IServiceCollection AddGameSessionRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var permitLimit = configuration.GetValue("RateLimits:GameSessions:PermitLimit", 30);
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    private static string ClientKey(HttpContext ctx)
    {
        var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded)) return forwarded.Split(',')[0].Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static ProblemHttpResult UnknownGame(string game) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: $"'{game}' does not use game sessions.");

    private static async Task<Results<Created<StartSessionResponse>, ValidationProblem, ProblemHttpResult>> StartAsync(
        string game, StartSessionRequest request, IGameSessionRepository repo, CancellationToken ct)
    {
        if (!SessionGames.TryGet(game, out var rules)) return UnknownGame(game);

        var errors = new ValidationErrors();
        ScoreRules.ValidatePlayer(errors, request.Nickname, request.Character, request.Color);
        ScoreRules.ValidateChallengeDate(errors, request.ChallengeDate, DateOnly.FromDateTime(DateTime.UtcNow));
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors.ToDictionary());

        DateOnly? challengeDate = ScoreRules.TryParseChallengeDate(request.ChallengeDate, out var d) ? d : null;
        var session = await repo.StartAsync(
            rules.Mode,
            new NewSession(ScoreRules.NormalizeNickname(request.Nickname), request.Character, request.Color, challengeDate),
            ct);
        return TypedResults.Created($"/api/games/{game}/sessions/{session.SessionId}", session);
    }

    private static async Task<Results<Ok<CompleteSessionResponse>, ValidationProblem, ProblemHttpResult>> CompleteAsync(
        string game, Guid id, CompleteSessionRequest request, IGameSessionRepository repo, CancellationToken ct)
    {
        if (!SessionGames.TryGet(game, out var rules)) return UnknownGame(game);

        var result = await repo.CompleteAsync(rules, id, request, ct);
        return result.Outcome switch
        {
            CompleteOutcome.Completed => TypedResults.Ok(result.Response!),
            CompleteOutcome.Invalid => TypedResults.ValidationProblem(result.Errors!),
            CompleteOutcome.NotFound => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Game session not found."),
            CompleteOutcome.Forbidden => TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This game session belongs to another player."),
            CompleteOutcome.Expired => TypedResults.Problem(statusCode: StatusCodes.Status410Gone, title: "This game session has expired."),
            CompleteOutcome.Conflict => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This game session was already completed with different results."),
            _ => throw new InvalidOperationException($"Unhandled outcome {result.Outcome}."),
        };
    }

    private static async Task<Results<Ok<PlayerGameStats>, ProblemHttpResult>> GetStatsAsync(
        string game, string nickname, IGameSessionRepository repo, CancellationToken ct)
    {
        if (!SessionGames.TryGet(game, out var rules)) return UnknownGame(game);
        return TypedResults.Ok(await repo.GetStatsAsync(rules.Mode, nickname, ct));
    }

    private static async Task<Results<Ok<IReadOnlyList<GameHistoryEntry>>, ProblemHttpResult>> GetHistoryAsync(
        string game, string nickname, int? limit, IGameSessionRepository repo, CancellationToken ct)
    {
        if (!SessionGames.TryGet(game, out var rules)) return UnknownGame(game);
        var take = Math.Clamp(limit ?? DefaultHistoryLimit, 1, MaxHistoryLimit);
        return TypedResults.Ok(await repo.GetHistoryAsync(rules.Mode, nickname, take, ct));
    }
}
