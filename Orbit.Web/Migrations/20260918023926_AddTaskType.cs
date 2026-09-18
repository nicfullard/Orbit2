using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited: the scaffolded default was "". The enum is stored as its name, so existing tasks must be
            // backfilled with "Task" or they could no longer be read at all.
            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Tasks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Task");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Type",
                table: "Tasks");
        }
    }
}
