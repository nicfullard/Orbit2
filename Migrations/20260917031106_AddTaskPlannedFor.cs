using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskPlannedFor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "PlannedFor",
                table: "Tasks",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_PlannedFor",
                table: "Tasks",
                column: "PlannedFor");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_PlannedFor",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "PlannedFor",
                table: "Tasks");
        }
    }
}
