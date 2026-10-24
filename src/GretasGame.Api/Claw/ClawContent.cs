using System.Reflection;
using System.Text.Json;

namespace GretasGame.Api.Claw;

/// <summary>
/// The prize catalog, level goals, worlds and avatar rules, embedded from the
/// web app's content (features/claw/content, exported with
/// <c>pnpm claw:export-content</c>). The API validates grabs and awards
/// avatars from exactly the same data the game plays with.
/// </summary>
public sealed class ClawContent
{
    public int Version { get; init; }
    public int MaxLevel { get; init; }
    public double MinGrabCycleSeconds { get; init; }
    public StarRule Stars { get; init; } = new(1, 3);
    public IReadOnlyList<Prize> Prizes { get; init; } = [];
    public IReadOnlyList<LevelDef> Levels { get; init; } = [];
    public IReadOnlyList<World> Worlds { get; init; } = [];
    public IReadOnlyList<Avatar> Avatars { get; init; } = [];

    public sealed record StarRule(int ThreeWithin, int TwoWithin);
    public sealed record Prize(string Id, string Name, string Rarity, int Points, int UnlockLevel, bool MysteryOnly);
    public sealed record LevelDef(int Level, string World, int Goal, int ToyCount);
    public sealed record World(string Id, string Name, int From, int To, string MysteryPrize, string Avatar);
    public sealed record Avatar(string Id, string Name, string Rarity, AvatarRule Rule);
    /// <summary>One of: firstGrab, prizesCollected(count), uniquePrizes(count), levelCleared(level), worldMystery(world).</summary>
    public sealed record AvatarRule(string Kind, int? Count, int? Level, string? World);

    private Dictionary<string, Prize>? prizeById;
    private Dictionary<int, LevelDef>? levelByNumber;
    private Dictionary<string, World>? worldById;
    private Dictionary<string, Avatar>? avatarById;

    public IReadOnlyDictionary<string, Prize> PrizeById => prizeById ??= Prizes.ToDictionary(p => p.Id, StringComparer.Ordinal);
    public IReadOnlyDictionary<int, LevelDef> LevelByNumber => levelByNumber ??= Levels.ToDictionary(l => l.Level);
    public IReadOnlyDictionary<string, World> WorldById => worldById ??= Worlds.ToDictionary(w => w.Id, StringComparer.Ordinal);
    public IReadOnlyDictionary<string, Avatar> AvatarById => avatarById ??= Avatars.ToDictionary(a => a.Id, StringComparer.Ordinal);

    public World? WorldForLevel(int level) => Worlds.FirstOrDefault(w => level >= w.From && level <= w.To);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<ClawContent> Instance = new(Load);

    /// <summary>The embedded content, parsed once.</summary>
    public static ClawContent Current => Instance.Value;

    public static ClawContent Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("claw-content.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("claw-content.json is not embedded.");
        var content = JsonSerializer.Deserialize<ClawContent>(stream, Json) ?? throw new InvalidOperationException("claw-content.json is empty.");
        content.Validate();
        return content;
    }

    /// <summary>Fails fast at startup if the export is inconsistent.</summary>
    public void Validate()
    {
        if (MaxLevel < 1) throw new InvalidOperationException("claw content: maxLevel must be positive.");
        if (Levels.Count != MaxLevel) throw new InvalidOperationException("claw content: one level definition per level is required.");
        if (Prizes.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != Prizes.Count) throw new InvalidOperationException("claw content: duplicate prize id.");
        foreach (var w in Worlds)
        {
            if (!PrizeById.ContainsKey(w.MysteryPrize)) throw new InvalidOperationException($"claw content: world {w.Id} mystery prize {w.MysteryPrize} is unknown.");
            if (!AvatarById.ContainsKey(w.Avatar)) throw new InvalidOperationException($"claw content: world {w.Id} avatar {w.Avatar} is unknown.");
        }
        foreach (var a in Avatars)
        {
            if (a.Rule.Kind is "worldMystery" && (a.Rule.World is null || !WorldById.ContainsKey(a.Rule.World)))
                throw new InvalidOperationException($"claw content: avatar {a.Id} references an unknown world.");
        }
    }
}
