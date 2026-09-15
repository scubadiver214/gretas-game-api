using FluentMigrator;

namespace GretasGame.Migrations;

[Migration(202609150001, "Create players table")]
public sealed class M202609150001_CreatePlayers : Migration
{
    public override void Up()
    {
        Create.Table("players")
            .WithColumn("id").AsGuid().PrimaryKey("pk_players")
            .WithColumn("nickname").AsString(16).NotNullable()
            // Lower-cased copy used for case-insensitive uniqueness without citext.
            .WithColumn("nickname_normalized").AsString(16).NotNullable()
            .WithColumn("character").AsString(16).NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.UniqueConstraint("ux_players_nickname_normalized")
            .OnTable("players")
            .Column("nickname_normalized");
    }

    public override void Down()
    {
        Delete.Table("players");
    }
}
