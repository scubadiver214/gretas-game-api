using FluentMigrator;

namespace GretasGame.Migrations;

/// <summary>
/// The public scoreboard now includes Wild Adventure Hunting. Rows come through
/// the ordinary score submit, one per finished level.
/// </summary>
[Migration(202610260001, "Allow hunting scores on the leaderboard")]
public sealed class M202610260001_AddHuntingScores : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TABLE scores DROP CONSTRAINT IF EXISTS ck_scores_mode;");
        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_mode CHECK (mode IN ('kitchen', 'delivery', 'wordle', 'frogger', 'claw', 'racing', 'hunting'));");
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM scores WHERE mode = 'hunting';");
        Execute.Sql("ALTER TABLE scores DROP CONSTRAINT IF EXISTS ck_scores_mode;");
        Execute.Sql("ALTER TABLE scores ADD CONSTRAINT ck_scores_mode CHECK (mode IN ('kitchen', 'delivery', 'wordle', 'frogger', 'claw', 'racing'));");
    }
}
