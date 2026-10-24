using System.Threading.RateLimiting;
using GretasGame.Api.Scores;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;

namespace GretasGame.Api.Claw;

/// <summary>
/// Bea's Claw-some Adventure: the server keeps the toy collection, avatars and
/// level progress. A level is an "attempt": start it, post each grab as it
/// resolves, complete it. Nicknames are not authenticated (same as every
/// other game here), so ownership checks are only as strong as the nickname.
/// </summary>
public static class ClawEndpoints
{
    public const string RateLimitPolicy = "claw";

    public static IEndpointRouteBuilder MapClawEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/claw").WithTags("Claw arcade");

        group.MapGet("/players/{nickname}/profile", GetProfileAsync)
            .WithName("GetClawProfile")
            .WithSummary("Current level, selected avatar, points, toy collection, avatars and cleared levels. Empty for unknown players.");

        group.MapPost("/players/{nickname}/attempts", StartAsync)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("StartClawAttempt")
            .WithSummary("Start a level (or a world's mystery machine). The level must be unlocked.");

        group.MapPost("/attempts/{id:guid}/grabs", RecordGrabAsync)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("RecordClawGrab")
            .WithSummary("Record one grab. Successful grabs add the prize to the collection and may unlock avatars. Safe to retry with the same seq.");

        group.MapPost("/attempts/{id:guid}/complete", CompleteAsync)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("CompleteClawAttempt")
            .WithSummary("Finish the attempt: stars, level progression and milestone avatars. Safe to retry.");

        group.MapPut("/players/{nickname}/avatar", SelectAvatarAsync)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("SelectClawAvatar")
            .WithSummary("Choose the avatar shown on the player's profile. Must be unlocked.");

        group.MapPut("/players/{nickname}/prizes/{prizeId}/favorite", SetFavoriteAsync)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("SetClawPrizeFavorite")
            .WithSummary("Star or un-star a collected prize.");

        return app;
    }

    /// <summary>A fixed window per client IP, keyed like the game-session limiter.</summary>
    public static IServiceCollection AddClawRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var permitLimit = configuration.GetValue("RateLimits:Claw:PermitLimit", 60);
        return services.AddRateLimiter(options =>
        {
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

    private static ProblemHttpResult Problem(ClawOutcome outcome) => outcome switch
    {
        ClawOutcome.NotFound => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Claw attempt not found."),
        ClawOutcome.Forbidden => TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This attempt belongs to another player."),
        ClawOutcome.Expired => TypedResults.Problem(statusCode: StatusCodes.Status410Gone, title: "This attempt has expired."),
        ClawOutcome.Conflict => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This attempt was already recorded with different results."),
        _ => throw new InvalidOperationException($"Unhandled outcome {outcome}."),
    };

    private static bool ValidNickname(string nickname, out ValidationProblem? problem)
    {
        var errors = new ValidationErrors();
        ScoreRules.ValidatePlayer(errors, nickname, "girl", null);
        problem = errors.Count > 0 ? TypedResults.ValidationProblem(errors.ToDictionary()) : null;
        return problem is null;
    }

    private static async Task<Results<Ok<ClawProfile>, ValidationProblem>> GetProfileAsync(string nickname, IClawRepository repo, CancellationToken ct)
    {
        if (!ValidNickname(nickname, out var problem)) return problem!;
        return TypedResults.Ok(await repo.GetProfileAsync(nickname, ct));
    }

    private static async Task<Results<Created<StartAttemptResponse>, ValidationProblem, ProblemHttpResult>> StartAsync(
        string nickname, StartAttemptRequest request, IClawRepository repo, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        ScoreRules.ValidatePlayer(errors, nickname, request.Character, request.Color);
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors.ToDictionary());

        var result = await repo.StartAsync(nickname, request, ct);
        return result.Outcome switch
        {
            ClawOutcome.Ok => TypedResults.Created($"/api/claw/attempts/{result.Response!.AttemptId}", result.Response),
            ClawOutcome.Invalid => TypedResults.ValidationProblem(result.Errors!),
            _ => Problem(result.Outcome),
        };
    }

    private static async Task<Results<Ok<RecordGrabResponse>, ValidationProblem, ProblemHttpResult>> RecordGrabAsync(
        Guid id, RecordGrabRequest request, IClawRepository repo, CancellationToken ct)
    {
        var result = await repo.RecordGrabAsync(id, request, ct);
        return result.Outcome switch
        {
            ClawOutcome.Ok => TypedResults.Ok(result.Response!),
            ClawOutcome.Invalid => TypedResults.ValidationProblem(result.Errors!),
            _ => Problem(result.Outcome),
        };
    }

    private static async Task<Results<Ok<CompleteAttemptResponse>, ValidationProblem, ProblemHttpResult>> CompleteAsync(
        Guid id, CompleteAttemptRequest request, IClawRepository repo, CancellationToken ct)
    {
        var result = await repo.CompleteAsync(id, request, ct);
        return result.Outcome switch
        {
            ClawOutcome.Ok => TypedResults.Ok(result.Response!),
            ClawOutcome.Invalid => TypedResults.ValidationProblem(result.Errors!),
            _ => Problem(result.Outcome),
        };
    }

    private static async Task<Results<Ok<ClawProfile>, ValidationProblem, ProblemHttpResult>> SelectAvatarAsync(
        string nickname, SelectAvatarRequest request, IClawRepository repo, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        ScoreRules.ValidatePlayer(errors, nickname, request.Character, request.Color);
        if (string.IsNullOrWhiteSpace(request.AvatarId)) errors.Add(nameof(request.AvatarId), "AvatarId is required.");
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors.ToDictionary());

        var result = await repo.SelectAvatarAsync(nickname, request, ct);
        return result.Outcome switch
        {
            ClawOutcome.Ok => TypedResults.Ok(result.Response!),
            ClawOutcome.Invalid => TypedResults.ValidationProblem(result.Errors!),
            _ => Problem(result.Outcome),
        };
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> SetFavoriteAsync(
        string nickname, string prizeId, FavoriteRequest request, IClawRepository repo, CancellationToken ct)
    {
        if (!ValidNickname(nickname, out var problem)) return problem!;
        var ok = await repo.SetFavoriteAsync(nickname, prizeId, request.Favorite, ct);
        return ok ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
