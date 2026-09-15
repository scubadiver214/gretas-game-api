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

    public static readonly IReadOnlySet<string> Modes = new HashSet<string>(StringComparer.Ordinal) { "kitchen", "delivery" };
    public static readonly IReadOnlySet<string> Characters = new HashSet<string>(StringComparer.Ordinal) { "boy", "girl" };

    /// <summary>Generous upper bounds so obviously forged scores are rejected.</summary>
    public static readonly IReadOnlyDictionary<string, int> MaxScoreByMode = new Dictionary<string, int>
    {
        ["kitchen"] = 300,
        ["delivery"] = 3000,
    };

    [GeneratedRegex(@"^[\p{L}\p{N} _'-]+$")]
    private static partial Regex NicknamePattern();

    public static string NormalizeNickname(string raw) =>
        WhitespaceRun().Replace(raw.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    public static string NicknameKey(string normalizedNickname) =>
        normalizedNickname.ToLowerInvariant();

    public static IReadOnlyDictionary<string, string[]> Validate(SubmitScoreRequest request)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list)) errors[field] = list = [];
            list.Add(message);
        }

        var nickname = NormalizeNickname(request.Nickname ?? string.Empty);
        if (nickname.Length < NicknameMin) Add(nameof(request.Nickname), $"Nickname must be at least {NicknameMin} characters.");
        if (nickname.Length > NicknameMax) Add(nameof(request.Nickname), $"Nickname must be at most {NicknameMax} characters.");
        if (nickname.Length > 0 && !NicknamePattern().IsMatch(nickname)) Add(nameof(request.Nickname), "Nickname may only contain letters, numbers, spaces, ' _ -");

        if (string.IsNullOrEmpty(request.Character) || !Characters.Contains(request.Character))
            Add(nameof(request.Character), $"Character must be one of: {string.Join(", ", Characters)}.");

        if (string.IsNullOrEmpty(request.Mode) || !Modes.Contains(request.Mode))
        {
            Add(nameof(request.Mode), $"Mode must be one of: {string.Join(", ", Modes)}.");
        }
        else
        {
            var max = MaxScoreByMode[request.Mode];
            if (request.Score < 0) Add(nameof(request.Score), "Score cannot be negative.");
            if (request.Score > max) Add(nameof(request.Score), $"Score cannot exceed {max} for {request.Mode}.");
        }

        if (request.DurationSeconds < 1 || request.DurationSeconds > MaxDurationSeconds)
            Add(nameof(request.DurationSeconds), $"Duration must be between 1 and {MaxDurationSeconds} seconds.");

        return errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    public static int ClampLimit(int? limit) =>
        Math.Clamp(limit ?? DefaultTopLimit, 1, MaxTopLimit);
}
