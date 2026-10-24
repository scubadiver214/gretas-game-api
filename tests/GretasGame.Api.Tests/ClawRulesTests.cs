using GretasGame.Api.Claw;
using GretasGame.Api.Scores;

namespace GretasGame.Api.Tests;

public class ClawRulesTests
{
    private static readonly ClawRules Rules = ClawRules.Default;
    private static readonly ClawContent Content = Rules.Content;

    private static IReadOnlyDictionary<string, string[]> Check(Action<ValidationErrors> validate)
    {
        var errors = new ValidationErrors();
        validate(errors);
        return errors.ToDictionary();
    }

    [Fact]
    public void Embedded_content_loads_and_is_consistent()
    {
        Assert.Equal(50, Content.MaxLevel);
        Assert.Equal(50, Content.Levels.Count);
        Assert.True(Content.Prizes.Count >= 45);
        Assert.Equal(5, Content.Worlds.Count);
        Assert.True(Content.MinGrabCycleSeconds > 0.5);
        Assert.Contains(Content.Avatars, a => a.Rule.Kind == "firstGrab");
        Assert.All(Content.Worlds, w => Assert.True(Content.PrizeById[w.MysteryPrize].MysteryOnly));
    }

    [Theory]
    [InlineData(1, 1, 3)]
    [InlineData(1, 2, 3)]
    [InlineData(1, 3, 2)]
    [InlineData(1, 4, 2)]
    [InlineData(1, 5, 1)]
    [InlineData(3, 12, 1)]
    public void Stars_match_the_web_rule(int goal, int grabs, int expected)
    {
        Assert.Equal(expected, Rules.Stars(goal, grabs));
    }

    [Fact]
    public void Start_rejects_locked_levels_and_accepts_unlocked_ones()
    {
        Assert.Empty(Check(e => Rules.ValidateStart(new StartAttemptRequest("girl", null, 3), currentLevel: 3, e)));
        Assert.Empty(Check(e => Rules.ValidateStart(new StartAttemptRequest("girl", null, 1), currentLevel: 3, e)));
        Assert.Contains("Level", Check(e => Rules.ValidateStart(new StartAttemptRequest("girl", null, 4), currentLevel: 3, e)).Keys);
        Assert.Contains("Level", Check(e => Rules.ValidateStart(new StartAttemptRequest("girl", null, 0), currentLevel: 3, e)).Keys);
        Assert.Contains("Level", Check(e => Rules.ValidateStart(new StartAttemptRequest("girl", null, 51), currentLevel: 50, e)).Keys);
    }

    [Fact]
    public void Mystery_machines_open_once_the_world_is_cleared()
    {
        var toybox = Content.WorldById["toybox"];
        Assert.Contains("Mystery", Check(e => Rules.ValidateStart(new StartAttemptRequest("girl", null, 0, "toybox"), currentLevel: toybox.To, e)).Keys);
        Assert.Empty(Check(e => Rules.ValidateStart(new StartAttemptRequest("girl", null, 0, "toybox"), currentLevel: toybox.To + 1, e)));
        Assert.Contains("Mystery", Check(e => Rules.ValidateStart(new StartAttemptRequest("girl", null, 0, "moon"), currentLevel: 50, e)).Keys);
    }

    [Fact]
    public void Grabs_must_be_numbered_in_order()
    {
        var ok = new RecordGrabRequest("Greta", Seq: 1, PrizeId: "teddy-brown", Succeeded: true, ElapsedSeconds: 4);
        Assert.Empty(Check(e => Rules.ValidateGrab(ok, 1, "level", null, grabsSoFar: 0, TimeSpan.FromSeconds(5), e)));
        Assert.Contains("Seq", Check(e => Rules.ValidateGrab(ok with { Seq = 2 }, 1, "level", null, grabsSoFar: 0, TimeSpan.FromSeconds(5), e)).Keys);
        Assert.Contains("Seq", Check(e => Rules.ValidateGrab(ok with { Seq = 0 }, 1, "level", null, grabsSoFar: 0, TimeSpan.FromSeconds(5), e)).Keys);
    }

    [Fact]
    public void Grabs_cannot_arrive_faster_than_the_claw_cycles()
    {
        var tenth = new RecordGrabRequest("Greta", Seq: 10, PrizeId: null, Succeeded: false, ElapsedSeconds: 1);
        Assert.Contains("Seq", Check(e => Rules.ValidateGrab(tenth, 1, "level", null, grabsSoFar: 9, TimeSpan.FromSeconds(2), e)).Keys);
        Assert.Empty(Check(e => Rules.ValidateGrab(tenth with { ElapsedSeconds = 30 }, 1, "level", null, grabsSoFar: 9, TimeSpan.FromSeconds(40), e)));
    }

    [Fact]
    public void Successful_grabs_need_a_prize_that_is_unlocked_on_that_level()
    {
        var rare = Content.Prizes.First(p => p.UnlockLevel > 20 && !p.MysteryOnly);
        var caught = new RecordGrabRequest("Greta", 1, rare.Id, true, 2);
        Assert.Contains("PrizeId", Check(e => Rules.ValidateGrab(caught, 1, "level", null, 0, TimeSpan.FromSeconds(3), e)).Keys);
        Assert.Empty(Check(e => Rules.ValidateGrab(caught, rare.UnlockLevel, "level", null, 0, TimeSpan.FromSeconds(3), e)));
        Assert.Contains("PrizeId", Check(e => Rules.ValidateGrab(caught with { PrizeId = "unicorn-made-of-cheese" }, 50, "level", null, 0, TimeSpan.FromSeconds(3), e)).Keys);
        Assert.Contains("PrizeId", Check(e => Rules.ValidateGrab(caught with { PrizeId = null }, 50, "level", null, 0, TimeSpan.FromSeconds(3), e)).Keys);
    }

    [Fact]
    public void Mystery_prizes_only_come_from_their_machine()
    {
        var toybox = Content.WorldById["toybox"];
        var caught = new RecordGrabRequest("Greta", 1, toybox.MysteryPrize, true, 2);
        Assert.Contains("PrizeId", Check(e => Rules.ValidateGrab(caught, 50, "level", null, 0, TimeSpan.FromSeconds(3), e)).Keys);
        Assert.Empty(Check(e => Rules.ValidateGrab(caught, toybox.To, "mystery", "toybox", 0, TimeSpan.FromSeconds(3), e)));
        Assert.Contains("PrizeId", Check(e => Rules.ValidateGrab(caught with { PrizeId = "teddy-brown" }, toybox.To, "mystery", "toybox", 0, TimeSpan.FromSeconds(3), e)).Keys);
    }

    [Fact]
    public void Elapsed_time_cannot_exceed_what_the_server_saw()
    {
        var grab = new RecordGrabRequest("Greta", 1, null, false, ElapsedSeconds: 60);
        Assert.Contains("ElapsedSeconds", Check(e => Rules.ValidateGrab(grab, 1, "level", null, 0, TimeSpan.FromSeconds(10), e)).Keys);
        var done = new CompleteAttemptRequest("Greta", DurationSeconds: 100);
        Assert.Contains("DurationSeconds", Check(e => Rules.ValidateComplete(done, TimeSpan.FromSeconds(30), e)).Keys);
        Assert.Empty(Check(e => Rules.ValidateComplete(done, TimeSpan.FromSeconds(98), e)));
        Assert.Contains("DurationSeconds", Check(e => Rules.ValidateComplete(done with { DurationSeconds = 0 }, TimeSpan.FromSeconds(98), e)).Keys);
    }

    [Fact]
    public void Milestones_mirror_the_web_rules()
    {
        var none = new HashSet<string>(StringComparer.Ordinal);
        Assert.Empty(Rules.EvaluateMilestones(new MilestoneContext(0, 0, 0, 0, none), none));
        Assert.Equal(["baby-bear"], Rules.EvaluateMilestones(new MilestoneContext(1, 1, 1, 0, none), none));
        var five = Rules.EvaluateMilestones(new MilestoneContext(5, 5, 2, 0, none), none);
        Assert.Equal(["baby-bear", "slime-buddy"], five);
        Assert.Empty(Rules.EvaluateMilestones(new MilestoneContext(5, 5, 2, 0, none), five.ToHashSet(StringComparer.Ordinal)));
        Assert.Contains("rainbow-unicorn", Rules.EvaluateMilestones(new MilestoneContext(0, 0, 0, 5, none), none));
        Assert.DoesNotContain("rainbow-unicorn", Rules.EvaluateMilestones(new MilestoneContext(0, 0, 0, 4, none), none));
        Assert.Contains("dino-champion", Rules.EvaluateMilestones(new MilestoneContext(30, 30, 25, 0, none), none));
        Assert.DoesNotContain("dino-champion", Rules.EvaluateMilestones(new MilestoneContext(30, 30, 3, 0, none), none));
        Assert.Equal(["race-driver"], Rules.EvaluateMilestones(new MilestoneContext(0, 0, 0, 0, new HashSet<string> { "toybox" }), none));
        Assert.Contains("claw-champion", Rules.EvaluateMilestones(new MilestoneContext(0, 0, 0, 50, none), none));
    }
}
