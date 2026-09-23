using FluentMigrator;

namespace GretasGame.Migrations;

/// <summary>
/// Adds the "wordle" mini-game as a valid score mode alongside kitchen/delivery.
/// </summary>
[Migration(202609150004, "Add wordle game mode")]
public sealed class M202609150004_AddWordleMode : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TABLE scores DROP CONSTRAINT IF EXISTS ck_scores_mode;");
        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_mode CHECK (mode IN ('kitchen', 'delivery', 'wordle'));");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE scores DROP CONSTRAINT IF EXISTS ck_scores_mode;");
        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_mode CHECK (mode IN ('kitchen', 'delivery'));");
    }
}
