using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <summary>
    /// Dependencies now exist only between tasks on a project (spec §6.15). Links on standalone tasks - which the old rule
    /// allowed within one department - would otherwise keep gating status changes with no page left to show or remove them,
    /// so this deletes them. Under the old rule both ends of such a link were standalone, so checking the successor is enough.
    /// Data only; nothing to restore on the way down.
    /// </summary>
    public partial class RemoveStandaloneDependencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "TaskDependencies" AS l
                USING "Tasks" AS t
                WHERE t."Id" = l."SuccessorTaskId" AND t."ProjectId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
