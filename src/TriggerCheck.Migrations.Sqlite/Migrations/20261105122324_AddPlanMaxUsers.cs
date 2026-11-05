using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriggerCheck.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanMaxUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxUsers",
                table: "Plans",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxUsers",
                table: "Plans");
        }
    }
}
