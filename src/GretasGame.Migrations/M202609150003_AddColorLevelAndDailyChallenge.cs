using FluentMigrator;

namespace GretasGame.Migrations;

/// <summary>
/// Player avatar colour, the difficulty level a score was earned at, and the
/// optional daily-challenge date a score belongs to.
/// </summary>
[Migration(202609150003, "Add player colour, score level and daily challenge date")]
public sealed class M202609150003_AddColorLevelAndDailyChallenge : Migration
{
    public override void Up()
    {
        Alter.Table("players")
            .AddColumn("color").AsString(7).Nullable();

        Alter.Table("scores")
            .AddColumn("level").AsInt32().NotNullable().WithDefaultValue(1)
            .AddColumn("challenge_date").AsDate().Nullable();

        Execute.Sql("ALTER TABLE players ADD CONSTRAINT ck_players_color CHECK (color IS NULL OR color ~ '^#[0-9A-Fa-f]{6}$');");
        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_level CHECK (level BETWEEN 1 AND 10);");

        // Daily leaderboards: one day's scores for a mode, best first.
        Create.Index("ix_scores_challenge_mode_score")
            .OnTable("scores")
            .OnColumn("challenge_date").Ascending()
            .OnColumn("mode").Ascending()
            .OnColumn("score").Descending()
            .OnColumn("created_at").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_scores_challenge_mode_score").OnTable("scores");
        Execute.Sql("ALTER TABLE scores DROP CONSTRAINT IF EXISTS ck_scores_level;");
        Execute.Sql("ALTER TABLE players DROP CONSTRAINT IF EXISTS ck_players_color;");
        Delete.Column("challenge_date").FromTable("scores");
        Delete.Column("level").FromTable("scores");
        Delete.Column("color").FromTable("players");
    }
}
