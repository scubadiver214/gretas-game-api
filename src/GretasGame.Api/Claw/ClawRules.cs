using GretasGame.Api.Scores;

namespace GretasGame.Api.Claw;

/// <summary>Everything an avatar rule can look at. Built from the player's tables.</summary>
public sealed record MilestoneContext(
    int SuccessfulGrabs,
    int PrizesCollected,
    int UniquePrizes,
    int HighestLevelCleared,
    IReadOnlySet<string> MysteryWorldsWon);

/// <summary>
/// Plausibility and progression rules for the claw game, mirroring the web
/// game's config.ts / engine (the numbers come from the embedded content).
/// The game runs on the client, so this rejects impossible results and
/// decides progression; it does not prove a prize was earned.
/// </summary>
public sealed class ClawRules(ClawContent content)
{
    /// <summary>The client may report a little more play time than the server saw.</summary>
    public const int DurationGraceSeconds = 5;
    /// <summary>Grab cadence is checked against this much slack (first grab, clock skew).</summary>
    public const double CadenceGraceSeconds = 3;
    public static readonly TimeSpan AttemptLifetime = TimeSpan.FromHours(6);
    public const int MaxDurationSeconds = 6 * 60 * 60;

    public ClawContent Content { get; } = content;

    public static ClawRules Default { get; } = new(ClawContent.Current);

    /// <summary>Stars for a cleared level: 3 within goal + 1 grabs, 2 within goal + 3, otherwise 1.</summary>
    public int Stars(int goal, int grabs)
    {
        if (grabs <= goal + Content.Stars.ThreeWithin) return 3;
        if (grabs <= goal + Content.Stars.TwoWithin) return 2;
        return 1;
    }

    public bool IsValidLevel(int level) => level >= 1 && level <= Content.MaxLevel;

    public void ValidateStart(StartAttemptRequest r, int currentLevel, ValidationErrors errors)
    {
        if (r.Mystery is not null)
        {
            if (!Content.WorldById.TryGetValue(r.Mystery, out var world))
            {
                errors.Add(nameof(r.Mystery), $"Mystery must be one of: {string.Join(", ", Content.Worlds.Select(w => w.Id))}.");
                return;
            }
            if (currentLevel <= world.To) errors.Add(nameof(r.Mystery), $"Clear level {world.To} to open the {world.Name} mystery machine.");
            return;
        }
        if (!IsValidLevel(r.Level)) errors.Add(nameof(r.Level), $"Level must be between 1 and {Content.MaxLevel}.");
        else if (r.Level > currentLevel) errors.Add(nameof(r.Level), $"Level {r.Level} is not unlocked yet (current level {currentLevel}).");
    }

    /// <summary>
    /// Checks a grab against the attempt: numbering, the prize's unlock level
    /// (or the mystery prize), and that grabs are not arriving faster than the
    /// claw can physically cycle.
    /// </summary>
    public void ValidateGrab(RecordGrabRequest r, int attemptLevel, string kind, string? world, int grabsSoFar, TimeSpan serverElapsed, ValidationErrors errors)
    {
        if (r.Seq < 1) errors.Add(nameof(r.Seq), "Seq must be 1 or more.");
        else if (r.Seq != grabsSoFar + 1) errors.Add(nameof(r.Seq), $"Expected grab {grabsSoFar + 1}, got {r.Seq}.");

        if (r.ElapsedSeconds < 0 || r.ElapsedSeconds > MaxDurationSeconds)
            errors.Add(nameof(r.ElapsedSeconds), $"ElapsedSeconds must be between 0 and {MaxDurationSeconds}.");
        else if (r.ElapsedSeconds > serverElapsed.TotalSeconds + DurationGraceSeconds)
            errors.Add(nameof(r.ElapsedSeconds), "ElapsedSeconds is longer than the attempt has been open.");

        var minElapsed = Math.Max(0, (r.Seq - 1) * Content.MinGrabCycleSeconds - CadenceGraceSeconds);
        if (serverElapsed.TotalSeconds < minElapsed)
            errors.Add(nameof(r.Seq), "Grabs are arriving faster than the claw can move.");

        if (r.Succeeded)
        {
            if (string.IsNullOrEmpty(r.PrizeId) || !Content.PrizeById.TryGetValue(r.PrizeId, out var prize))
            {
                errors.Add(nameof(r.PrizeId), "PrizeId must name a prize in the catalog.");
                return;
            }
            if (kind == "mystery")
            {
                var w = world is null ? null : Content.WorldById.GetValueOrDefault(world);
                if (w is null || !string.Equals(w.MysteryPrize, prize.Id, StringComparison.Ordinal))
                    errors.Add(nameof(r.PrizeId), "That prize is not in this mystery machine.");
            }
            else
            {
                if (prize.MysteryOnly) errors.Add(nameof(r.PrizeId), "That prize only comes from a mystery machine.");
                else if (prize.UnlockLevel > attemptLevel) errors.Add(nameof(r.PrizeId), $"{prize.Name} is not available until level {prize.UnlockLevel}.");
            }
        }
        else if (r.PrizeId is not null && !Content.PrizeById.ContainsKey(r.PrizeId))
        {
            errors.Add(nameof(r.PrizeId), "PrizeId must name a prize in the catalog.");
        }
    }

    public void ValidateComplete(CompleteAttemptRequest r, TimeSpan serverElapsed, ValidationErrors errors)
    {
        if (r.DurationSeconds < 1 || r.DurationSeconds > MaxDurationSeconds)
            errors.Add(nameof(r.DurationSeconds), $"DurationSeconds must be between 1 and {MaxDurationSeconds}.");
        else if (r.DurationSeconds > serverElapsed.TotalSeconds + DurationGraceSeconds)
            errors.Add(nameof(r.DurationSeconds), "DurationSeconds is longer than the attempt has been open.");
    }

    public bool MeetsRule(ClawContent.AvatarRule rule, MilestoneContext ctx) => rule.Kind switch
    {
        "firstGrab" => ctx.SuccessfulGrabs >= 1,
        "prizesCollected" => ctx.PrizesCollected >= (rule.Count ?? int.MaxValue),
        "uniquePrizes" => ctx.UniquePrizes >= (rule.Count ?? int.MaxValue),
        "levelCleared" => ctx.HighestLevelCleared >= (rule.Level ?? int.MaxValue),
        "worldMystery" => rule.World is not null && ctx.MysteryWorldsWon.Contains(rule.World),
        _ => false,
    };

    /// <summary>Ids of avatars newly earned (excluding already-unlocked ones), in catalog order.</summary>
    public IReadOnlyList<string> EvaluateMilestones(MilestoneContext ctx, IReadOnlySet<string> unlocked) =>
        Content.Avatars.Where(a => !unlocked.Contains(a.Id) && MeetsRule(a.Rule, ctx)).Select(a => a.Id).ToList();
}
