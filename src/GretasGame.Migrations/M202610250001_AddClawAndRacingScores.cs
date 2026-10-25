using FluentMigrator;

namespace GretasGame.Migrations;

/// <summary>
/// The public scoreboard now includes Bea's Claw-some Adventure and The Fitz
/// and Furious. Claw rows are written when an attempt completes; racing rows
/// come through the ordinary score submit.
/// </summary>
[Migration(202610250001, "Allow claw and racing scores on the leaderboard")]
public sealed class M202610250001_AddClawAndRacingScores : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TABLE scores DROP CONSTRAINT IF EXISTS ck_scores_mode;");
        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_mode CHECK (mode IN ('kitchen', 'delivery', 'wordle', 'frogger', 'claw', 'racing'));");
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM scores WHERE mode IN ('claw', 'racing');");
        Execute.Sql("ALTER TABLE scores DROP CONSTRAINT IF EXISTS ck_scores_mode;");
        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_mode CHECK (mode IN ('kitchen', 'delivery', 'wordle', 'frogger'));");
    }
}
