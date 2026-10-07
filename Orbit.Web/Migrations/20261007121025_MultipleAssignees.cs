using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class MultipleAssignees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Several assignees on a task and on a recurring definition (§6.2.3). The tables come first and are filled from the
            // AssigneeId columns before those are dropped - the order EF scaffolds (drop, then create) would lose every assignee.
            migrationBuilder.CreateTable(
                name: "RecurringTaskAssignments",
                columns: table => new
                {
                    RecurringTaskDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringTaskAssignments", x => new { x.RecurringTaskDefinitionId, x.UserId });
                    table.ForeignKey(
                        name: "FK_RecurringTaskAssignments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringTaskAssignments_RecurringTaskDefinitions_Recurring~",
                        column: x => x.RecurringTaskDefinitionId,
                        principalTable: "RecurringTaskDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskAssignments",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AssignedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskAssignments", x => new { x.TaskId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TaskAssignments_AspNetUsers_AssignedById",
                        column: x => x.AssignedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskAssignments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskAssignments_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringTaskAssignments_UserId",
                table: "RecurringTaskAssignments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignments_AssignedById",
                table: "TaskAssignments",
                column: "AssignedById");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAssignments_UserId",
                table: "TaskAssignments",
                column: "UserId");

            // Every assigned task keeps its assignee as its one assignment. When and by whom it was assigned isn't on the task:
            // the last change of assignee in its audit trail that named this person says (the person who made it, when a user
            // did). A task with no such entry was assigned as it was created, so it counts from then, by its creator - except a
            // task a recurring definition generated, which nobody assigned.
            migrationBuilder.Sql("""
                INSERT INTO "TaskAssignments" ("TaskId", "UserId", "AssignedAt", "AssignedById")
                SELECT t."Id", t."AssigneeId", COALESCE(a."Timestamp", t."CreatedAt"),
                       CASE WHEN a."Timestamp" IS NOT NULL THEN a."ById"
                            WHEN t."Source" <> 'Recurring' THEN t."CreatedById" END
                FROM "Tasks" t
                LEFT JOIN LATERAL (
                    SELECT l."Timestamp", u."Id" AS "ById"
                    FROM "AuditLogs" l
                    LEFT JOIN "AspNetUsers" u ON u."Id" = l."ActorId" AND l."ActorType" = 'User'
                    WHERE l."EntityType" = 'Task' AND l."EntityId" = t."Id"
                      AND l."Details" -> 'assigneeId' ->> 'to' = t."AssigneeId"::text
                    ORDER BY l."Timestamp" DESC
                    LIMIT 1) a ON true
                WHERE t."AssigneeId" IS NOT NULL;

                INSERT INTO "RecurringTaskAssignments" ("RecurringTaskDefinitionId", "UserId")
                SELECT "Id", "AssigneeId" FROM "RecurringTaskDefinitions" WHERE "AssigneeId" IS NOT NULL;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringTaskDefinitions_AspNetUsers_AssigneeId",
                table: "RecurringTaskDefinitions");

            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_AspNetUsers_AssigneeId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_AssigneeId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_RecurringTaskDefinitions_AssigneeId",
                table: "RecurringTaskDefinitions");

            migrationBuilder.DropColumn(
                name: "AssigneeId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "AssigneeId",
                table: "RecurringTaskDefinitions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssigneeId",
                table: "Tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssigneeId",
                table: "RecurringTaskDefinitions",
                type: "uuid",
                nullable: true);

            // The previous build knows one assignee. Of several, the one who stays is an active person before a deactivated
            // one (that build refuses every edit over MCP to a task whose assignee is deactivated), then whoever has been on
            // the task longest - which, for a task that had an assignee before the upgrade, is that person - then the lowest
            // user id. The others are dropped; the task's audit trail still names them. A definition's assignments carry no
            // date, so there it is the first active person by name.
            migrationBuilder.Sql("""
                UPDATE "Tasks" t SET "AssigneeId" = s."UserId"
                FROM (SELECT DISTINCT ON (x."TaskId") x."TaskId", x."UserId"
                      FROM "TaskAssignments" x JOIN "AspNetUsers" u ON u."Id" = x."UserId"
                      ORDER BY x."TaskId", u."IsActive" DESC, x."AssignedAt", x."UserId") s
                WHERE t."Id" = s."TaskId";

                UPDATE "RecurringTaskDefinitions" r SET "AssigneeId" = s."UserId"
                FROM (SELECT DISTINCT ON (x."RecurringTaskDefinitionId") x."RecurringTaskDefinitionId", x."UserId"
                      FROM "RecurringTaskAssignments" x JOIN "AspNetUsers" u ON u."Id" = x."UserId"
                      ORDER BY x."RecurringTaskDefinitionId", u."IsActive" DESC, u."DisplayName", x."UserId") s
                WHERE r."Id" = s."RecurringTaskDefinitionId";
                """);

            migrationBuilder.DropTable(
                name: "RecurringTaskAssignments");

            migrationBuilder.DropTable(
                name: "TaskAssignments");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_AssigneeId",
                table: "Tasks",
                column: "AssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringTaskDefinitions_AssigneeId",
                table: "RecurringTaskDefinitions",
                column: "AssigneeId");

            migrationBuilder.AddForeignKey(
                name: "FK_RecurringTaskDefinitions_AspNetUsers_AssigneeId",
                table: "RecurringTaskDefinitions",
                column: "AssigneeId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_AspNetUsers_AssigneeId",
                table: "Tasks",
                column: "AssigneeId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
