using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class RequestTaskDueOnCreation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A task step may make its task due the day it is created (spec §6.20, §13 item 73). Existing steps keep what they
            // had: no due date, or the Date field DueDateFieldId names.
            migrationBuilder.AddColumn<bool>(
                name: "DueOnCreation",
                table: "RequestFlowSteps",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A step that was due on creation goes back to no due date. Tasks already created keep theirs.
            migrationBuilder.DropColumn(
                name: "DueOnCreation",
                table: "RequestFlowSteps");
        }
    }
}
