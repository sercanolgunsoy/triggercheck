using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriggerCheck.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SplitSmsAndVoicePlanFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "AllowsSmsVoice",
                table: "Plans",
                newName: "AllowsVoiceCall");

            migrationBuilder.AddColumn<bool>(
                name: "AllowsSms",
                table: "Plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The old flag allowed both SMS and voice calls: plans that had it keep both (AllowsVoiceCall is the
            // renamed column). New defaults (SMS from Standard, voice from Pro) only apply to newly seeded plans.
            migrationBuilder.Sql("UPDATE \"Plans\" SET \"AllowsSms\" = \"AllowsVoiceCall\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The single old flag is set when the plan allowed either channel.
            migrationBuilder.Sql("UPDATE \"Plans\" SET \"AllowsVoiceCall\" = (\"AllowsVoiceCall\" OR \"AllowsSms\");");

            migrationBuilder.DropColumn(
                name: "AllowsSms",
                table: "Plans");

            migrationBuilder.RenameColumn(
                name: "AllowsVoiceCall",
                table: "Plans",
                newName: "AllowsSmsVoice");
        }
    }
}
