using FluentMigrator;

namespace GretasGame.Migrations;

[Migration(202609150002, "Create scores table")]
public sealed class M202609150002_CreateScores : Migration
{
    public override void Up()
    {
        Create.Table("scores")
            .WithColumn("id").AsGuid().PrimaryKey("pk_scores")
            .WithColumn("player_id").AsGuid().NotNullable()
                .ForeignKey("fk_scores_players", "players", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("mode").AsString(16).NotNullable()
            .WithColumn("score").AsInt32().NotNullable()
            .WithColumn("duration_seconds").AsInt32().NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        // Leaderboard reads: top N per mode ordered by score desc, earliest first on ties.
        Create.Index("ix_scores_mode_score_created")
            .OnTable("scores")
            .OnColumn("mode").Ascending()
            .OnColumn("score").Descending()
            .OnColumn("created_at").Ascending();

        Create.Index("ix_scores_player_mode")
            .OnTable("scores")
            .OnColumn("player_id").Ascending()
            .OnColumn("mode").Ascending();

        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_score_nonnegative CHECK (score >= 0);");
        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_mode CHECK (mode IN ('kitchen', 'delivery'));");
    }

    public override void Down()
    {
        Delete.Table("scores");
    }
}
