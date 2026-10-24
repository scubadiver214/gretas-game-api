using FluentMigrator;

namespace GretasGame.Migrations;

/// <summary>
/// Bea's Claw-some Adventure: a per-player profile (current level, chosen avatar,
/// points), the toy collection, unlocked avatars, cleared levels, and the
/// server-tracked attempts and grabs that feed them. Nothing here touches
/// <c>scores</c>: the claw game has no public leaderboard.
/// </summary>
[Migration(202610240001, "Add the claw arcade tables")]
public sealed class M202610240001_AddClawArcade : Migration
{
    public override void Up()
    {
        Create.Table("claw_profiles")
            .WithColumn("player_id").AsGuid().PrimaryKey("pk_claw_profiles")
                .ForeignKey("fk_claw_profiles_players", "players", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("current_level").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("selected_avatar").AsString(32).Nullable()
            .WithColumn("points").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("total_grabs").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("successful_grabs").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        Execute.Sql("ALTER TABLE claw_profiles ADD CONSTRAINT ck_claw_profiles_level CHECK (current_level BETWEEN 1 AND 50);");
        Execute.Sql("ALTER TABLE claw_profiles ADD CONSTRAINT ck_claw_profiles_counts CHECK (points >= 0 AND total_grabs >= 0 AND successful_grabs BETWEEN 0 AND total_grabs);");

        Create.Table("claw_prizes")
            .WithColumn("player_id").AsGuid().NotNullable()
                .ForeignKey("fk_claw_prizes_players", "players", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("prize_id").AsString(48).NotNullable()
            .WithColumn("count").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("favorite").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("first_collected_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_collected_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        Create.PrimaryKey("pk_claw_prizes").OnTable("claw_prizes").Columns("player_id", "prize_id");
        Execute.Sql("ALTER TABLE claw_prizes ADD CONSTRAINT ck_claw_prizes_count CHECK (count >= 1);");

        Create.Table("claw_avatars")
            .WithColumn("player_id").AsGuid().NotNullable()
                .ForeignKey("fk_claw_avatars_players", "players", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("avatar_id").AsString(32).NotNullable()
            .WithColumn("unlocked_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        Create.PrimaryKey("pk_claw_avatars").OnTable("claw_avatars").Columns("player_id", "avatar_id");

        // Best result per cleared level, for the level picker's stars.
        Create.Table("claw_levels")
            .WithColumn("player_id").AsGuid().NotNullable()
                .ForeignKey("fk_claw_levels_players", "players", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("level").AsInt32().NotNullable()
            .WithColumn("stars").AsInt32().NotNullable()
            .WithColumn("best_grabs").AsInt32().NotNullable()
            .WithColumn("clears").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("first_cleared_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_cleared_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        Create.PrimaryKey("pk_claw_levels").OnTable("claw_levels").Columns("player_id", "level");
        Execute.Sql("ALTER TABLE claw_levels ADD CONSTRAINT ck_claw_levels_stars CHECK (stars BETWEEN 1 AND 3);");

        Create.Table("claw_attempts")
            .WithColumn("id").AsGuid().PrimaryKey("pk_claw_attempts")
            .WithColumn("player_id").AsGuid().NotNullable()
                .ForeignKey("fk_claw_attempts_players", "players", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("level").AsInt32().NotNullable()
            .WithColumn("kind").AsString(16).NotNullable().WithDefaultValue("level")
            .WithColumn("world").AsString(16).Nullable()
            .WithColumn("status").AsString(16).NotNullable().WithDefaultValue("active")
            .WithColumn("started_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("completed_at").AsDateTimeOffset().Nullable()
            .WithColumn("grabs").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("successes").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("stars").AsInt32().Nullable()
            .WithColumn("duration_seconds").AsInt32().Nullable();
        Execute.Sql("ALTER TABLE claw_attempts ADD CONSTRAINT ck_claw_attempts_kind CHECK (kind IN ('level', 'mystery'));");
        Execute.Sql("ALTER TABLE claw_attempts ADD CONSTRAINT ck_claw_attempts_status CHECK (status IN ('active', 'completed'));");
        Execute.Sql("ALTER TABLE claw_attempts ADD CONSTRAINT ck_claw_attempts_level CHECK (level BETWEEN 1 AND 50);");
        Execute.Sql("ALTER TABLE claw_attempts ADD CONSTRAINT ck_claw_attempts_counts CHECK (grabs >= 0 AND successes BETWEEN 0 AND grabs);");
        Create.Index("ix_claw_attempts_player_completed")
            .OnTable("claw_attempts")
            .OnColumn("player_id").Ascending()
            .OnColumn("completed_at").Descending();
        Execute.Sql("CREATE INDEX ix_claw_attempts_active_started ON claw_attempts (started_at) WHERE status = 'active';");

        Create.Table("claw_grabs")
            .WithColumn("id").AsGuid().PrimaryKey("pk_claw_grabs")
            .WithColumn("attempt_id").AsGuid().NotNullable()
                .ForeignKey("fk_claw_grabs_attempts", "claw_attempts", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("client_seq").AsInt32().NotNullable()
            .WithColumn("prize_id").AsString(48).Nullable()
            .WithColumn("succeeded").AsBoolean().NotNullable()
            .WithColumn("recorded_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        // One row per client grab number: retries of the same grab are no-ops.
        Create.UniqueConstraint("ux_claw_grabs_attempt_seq").OnTable("claw_grabs").Columns("attempt_id", "client_seq");
        Execute.Sql("ALTER TABLE claw_grabs ADD CONSTRAINT ck_claw_grabs_prize CHECK (NOT succeeded OR prize_id IS NOT NULL);");
    }

    public override void Down()
    {
        Delete.Table("claw_grabs");
        Delete.Table("claw_attempts");
        Delete.Table("claw_levels");
        Delete.Table("claw_avatars");
        Delete.Table("claw_prizes");
        Delete.Table("claw_profiles");
    }
}
