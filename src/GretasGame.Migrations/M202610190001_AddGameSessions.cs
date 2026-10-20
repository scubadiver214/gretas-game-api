using FluentMigrator;

namespace GretasGame.Migrations;

/// <summary>
/// Server-tracked game sessions for arcade games whose scores can't be checked
/// from a single submit (starting with Frogger). A completed session writes one
/// ordinary <c>scores</c> row, linked back through <c>scores.session_id</c>, so
/// the existing leaderboards and personal bests pick it up unchanged.
/// </summary>
[Migration(202610190001, "Add game sessions and the frogger mode")]
public sealed class M202610190001_AddGameSessions : Migration
{
    public override void Up()
    {
        Create.Table("game_sessions")
            .WithColumn("id").AsGuid().PrimaryKey("pk_game_sessions")
            .WithColumn("player_id").AsGuid().NotNullable()
                .ForeignKey("fk_game_sessions_players", "players", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("mode").AsString(16).NotNullable()
            .WithColumn("status").AsString(16).NotNullable().WithDefaultValue("active")
            .WithColumn("challenge_date").AsDate().Nullable()
            .WithColumn("started_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("completed_at").AsDateTimeOffset().Nullable()
            .WithColumn("final_score").AsInt32().Nullable()
            .WithColumn("highest_level").AsInt32().Nullable()
            .WithColumn("levels_completed").AsInt32().Nullable()
            .WithColumn("lives_lost").AsInt32().Nullable()
            .WithColumn("duration_seconds").AsInt32().Nullable();

        Execute.Sql("ALTER TABLE game_sessions ADD CONSTRAINT ck_game_sessions_mode CHECK (mode IN ('frogger'));");
        Execute.Sql("ALTER TABLE game_sessions ADD CONSTRAINT ck_game_sessions_status CHECK (status IN ('active', 'completed'));");
        Execute.Sql("ALTER TABLE game_sessions ADD CONSTRAINT ck_game_sessions_final_score CHECK (final_score IS NULL OR final_score >= 0);");
        // A completed session always carries its results.
        Execute.Sql(
            """
            ALTER TABLE game_sessions ADD CONSTRAINT ck_game_sessions_completed_results CHECK (
                status <> 'completed'
                OR (completed_at IS NOT NULL AND final_score IS NOT NULL AND highest_level IS NOT NULL
                    AND levels_completed IS NOT NULL AND lives_lost IS NOT NULL AND duration_seconds IS NOT NULL));
            """);

        // Player history and stats: one player's finished games for a mode, newest first.
        Create.Index("ix_game_sessions_player_mode_completed")
            .OnTable("game_sessions")
            .OnColumn("player_id").Ascending()
            .OnColumn("mode").Ascending()
            .OnColumn("completed_at").Descending();

        // Open sessions only: expiry checks and a future cleanup job.
        Execute.Sql("CREATE INDEX ix_game_sessions_active_started ON game_sessions (started_at) WHERE status = 'active';");

        Alter.Table("scores")
            .AddColumn("session_id").AsGuid().Nullable()
                .ForeignKey("fk_scores_game_sessions", "game_sessions", "id");
        // At most one score per session (NULLs - scores without a session - don't collide).
        Create.UniqueConstraint("ux_scores_session_id").OnTable("scores").Column("session_id");

        Execute.Sql("ALTER TABLE scores DROP CONSTRAINT IF EXISTS ck_scores_mode;");
        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_mode CHECK (mode IN ('kitchen', 'delivery', 'wordle', 'frogger'));");
        // Frogger levels keep climbing; the other games stay within 1-5 via API validation.
        Execute.Sql("ALTER TABLE scores DROP CONSTRAINT IF EXISTS ck_scores_level;");
        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_level CHECK (level BETWEEN 1 AND 99);");
    }

    public override void Down()
    {
        // The old constraints can't hold frogger rows, so rolling back discards them.
        Execute.Sql("DELETE FROM scores WHERE mode = 'frogger';");
        Execute.Sql("ALTER TABLE scores DROP CONSTRAINT IF EXISTS ck_scores_level;");
        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_level CHECK (level BETWEEN 1 AND 10);");
        Execute.Sql("ALTER TABLE scores DROP CONSTRAINT IF EXISTS ck_scores_mode;");
        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_mode CHECK (mode IN ('kitchen', 'delivery', 'wordle'));");

        Delete.UniqueConstraint("ux_scores_session_id").FromTable("scores");
        Delete.ForeignKey("fk_scores_game_sessions").OnTable("scores");
        Delete.Column("session_id").FromTable("scores");
        Delete.Table("game_sessions");
    }
}
