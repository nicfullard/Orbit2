using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class IndependentClocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RunningClocks_UserId",
                table: "RunningClocks");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                table: "RunningClocks",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // A clock running at the upgrade counts as seen now, so the sweep gives its page time to check in (or, for a page
            // loaded before the upgrade, which never will, stops it at the upgrade) rather than logging it at year 1.
            migrationBuilder.Sql("""UPDATE "RunningClocks" SET "LastSeenAt" = now();""");

            // One clock per user per task (§6.10, §13 item 64), so the clocks on different tasks run independently.
            migrationBuilder.CreateIndex(
                name: "IX_RunningClocks_UserId_TaskId",
                table: "RunningClocks",
                columns: new[] { "UserId", "TaskId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RunningClocks_UserId_TaskId",
                table: "RunningClocks");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "RunningClocks");

            // The previous build allows one clock per user: keep each user's most recently started and drop the rest, unlogged.
            migrationBuilder.Sql("""
                DELETE FROM "RunningClocks" c
                WHERE EXISTS (
                    SELECT 1 FROM "RunningClocks" o
                    WHERE o."UserId" = c."UserId"
                      AND (o."StartedAt" > c."StartedAt" OR (o."StartedAt" = c."StartedAt" AND o."Id" > c."Id")));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RunningClocks_UserId",
                table: "RunningClocks",
                column: "UserId",
                unique: true);
        }
    }
}
