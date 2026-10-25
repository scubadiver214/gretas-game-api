using System.Globalization;
using System.Text.RegularExpressions;

namespace GretasGame.Api.Scores;

/// <summary>
/// Pure validation/normalisation rules shared by the endpoints and tests.
/// Mirrors the constraints in the web client (features/player/types.ts).
/// </summary>
public static partial class ScoreRules
{
    public const int NicknameMin = 2;
    public const int NicknameMax = 16;
    public const int DefaultTopLimit = 20;
    public const int MaxTopLimit = 100;
    public const int MaxDurationSeconds = 600;
    public const int MinLevel = 1;
    public const int MaxLevel = 5;
    /// <summary>Daily-challenge submissions may be up to this many days old (time zones, slow finishes).</summary>
    public const int ChallengeDateGraceDays = 1;

    /// <summary>Every game with a public leaderboard.</summary>
    public static readonly IReadOnlySet<string> LeaderboardModes = new HashSet<string>(StringComparer.Ordinal)
    {
        "kitchen", "delivery", "wordle", "frogger", "claw", "racing",
    };

    /// <summary>
    /// Games whose clients post a finished round straight to <c>POST /api/scores</c>.
    /// The rest go through a server-tracked attempt (frogger sessions, claw arcade).
    /// </summary>
    public static readonly IReadOnlySet<string> DirectSubmitModes = new HashSet<string>(StringComparer.Ordinal)
    {
        "kitchen", "delivery", "wordle", "racing",
    };

    public static readonly IReadOnlySet<string> Characters = new HashSet<string>(StringComparer.Ordinal) { "boy", "girl" };

    /// <summary>Generous upper bounds so obviously forged scores are rejected.</summary>
    public static readonly IReadOnlyDictionary<string, int> MaxScoreByMode = new Dictionary<string, int>
    {
        // Lunch Rush: a flawless level-5 shift (~30 premium pizzas at the x3 combo) stays under 14,000.
        ["kitchen"] = 20000,
        // 60 s at max speed is roughly 100 mailboxes x 35 with combos.
        ["delivery"] = 5000,
        // Level 5: 5 words x (100 base + 20 length bonus) solved first try = 600.
        ["wordle"] = 800,
        // Finish bonus 5,000 + coins + gems + a 300 s time bonus stays under 15,000.
        ["racing"] = 20000,
    };

    [GeneratedRegex(@"^[\p{L}\p{N} _'-]+$")]
    private static partial Regex NicknamePattern();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    public static string NormalizeNickname(string raw) =>
        WhitespaceRun().Replace(raw.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    public static string NicknameKey(string normalizedNickname) =>
        normalizedNickname.ToLowerInvariant();

    public static bool IsValidColor(string? color) =>
        color is not null && ColorPattern().IsMatch(color);

    /// <summary>Parses a yyyy-MM-dd challenge date key.</summary>
    public static bool TryParseChallengeDate(string? raw, out DateOnly date) =>
        DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>A challenge date must be today (UTC) or within the grace window.</summary>
    public static bool IsChallengeDateOpen(DateOnly date, DateOnly todayUtc) =>
        date <= todayUtc && todayUtc.DayNumber - date.DayNumber <= ChallengeDateGraceDays;

    public static IReadOnlyDictionary<string, string[]> Validate(SubmitScoreRequest request) =>
        Validate(request, DateOnly.FromDateTime(DateTime.UtcNow));

    public static IReadOnlyDictionary<string, string[]> Validate(SubmitScoreRequest request, DateOnly todayUtc)
    {
        var errors = new ValidationErrors();

        ValidatePlayer(errors, request.Nickname, request.Character, request.Color);

        if (string.IsNullOrEmpty(request.Mode) || !LeaderboardModes.Contains(request.Mode))
        {
            errors.Add(nameof(request.Mode), $"Mode must be one of: {string.Join(", ", LeaderboardModes)}.");
        }
        else if (!DirectSubmitModes.Contains(request.Mode))
        {
            errors.Add(nameof(request.Mode), $"{request.Mode} scores are saved through game sessions (POST /api/games/{request.Mode}/sessions).");
        }
        else
        {
            var max = MaxScoreByMode[request.Mode];
            if (request.Score < 0) errors.Add(nameof(request.Score), "Score cannot be negative.");
            if (request.Score > max) errors.Add(nameof(request.Score), $"Score cannot exceed {max} for {request.Mode}.");
        }

        if (request.Level < MinLevel || request.Level > MaxLevel)
            errors.Add(nameof(request.Level), $"Level must be between {MinLevel} and {MaxLevel}.");

        if (request.DurationSeconds < 1 || request.DurationSeconds > MaxDurationSeconds)
            errors.Add(nameof(request.DurationSeconds), $"Duration must be between 1 and {MaxDurationSeconds} seconds.");

        ValidateChallengeDate(errors, request.ChallengeDate, todayUtc);

        return errors.ToDictionary();
    }

    /// <summary>Nickname, character and optional colour checks shared by every write endpoint.</summary>
    public static void ValidatePlayer(ValidationErrors errors, string? rawNickname, string? character, string? color)
    {
        var nickname = NormalizeNickname(rawNickname ?? string.Empty);
        if (nickname.Length < NicknameMin) errors.Add("Nickname", $"Nickname must be at least {NicknameMin} characters.");
        if (nickname.Length > NicknameMax) errors.Add("Nickname", $"Nickname must be at most {NicknameMax} characters.");
        if (nickname.Length > 0 && !NicknamePattern().IsMatch(nickname)) errors.Add("Nickname", "Nickname may only contain letters, numbers, spaces, ' _ -");

        if (string.IsNullOrEmpty(character) || !Characters.Contains(character))
            errors.Add("Character", $"Character must be one of: {string.Join(", ", Characters)}.");

        if (color is not null && !IsValidColor(color))
            errors.Add("Color", "Color must be a #RRGGBB hex value.");
    }

    public static void ValidateChallengeDate(ValidationErrors errors, string? challengeDate, DateOnly todayUtc)
    {
        if (challengeDate is null) return;
        if (!TryParseChallengeDate(challengeDate, out var date))
            errors.Add("ChallengeDate", "ChallengeDate must be a yyyy-MM-dd date.");
        else if (!IsChallengeDateOpen(date, todayUtc))
            errors.Add("ChallengeDate", "That daily challenge is closed.");
    }

    public static int ClampLimit(int? limit) =>
        Math.Clamp(limit ?? DefaultTopLimit, 1, MaxTopLimit);
}

/// <summary>Collects field errors in the shape <c>TypedResults.ValidationProblem</c> expects.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> errors = new();

    public int Count => errors.Count;

    public void Add(string field, string message)
    {
        if (!errors.TryGetValue(field, out var list)) errors[field] = list = [];
        list.Add(message);
    }

    public IReadOnlyDictionary<string, string[]> ToDictionary() =>
        errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
}
