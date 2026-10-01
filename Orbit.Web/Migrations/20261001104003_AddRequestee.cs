using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tasks.RequestedForId becomes RequesteeId (spec §6.2.2): the person a task is for, now on any task, beside its assignee.
            // A rename, so every request's requestee is kept.
            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_AspNetUsers_RequestedForId",
                table: "Tasks");

            migrationBuilder.RenameColumn(
                name: "RequestedForId",
                table: "Tasks",
                newName: "RequesteeId");

            migrationBuilder.RenameIndex(
                name: "IX_Tasks_RequestedForId",
                table: "Tasks",
                newName: "IX_Tasks_RequesteeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_AspNetUsers_RequesteeId",
                table: "Tasks",
                column: "RequesteeId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // The shipped roles' default tasks.create_for grant (spec §6.5), for a database whose roles already exist - a new database
            // gets it from DbInitializer. Own names nobody else, so nothing changes for anyone until an admin widens it; a shipped role
            // that has been renamed or deleted is left alone, and the built-in role needs no rows.
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleId", "Permission", "Scope")
                SELECT r."Id", g.permission, g.scope
                FROM "AspNetRoles" r
                JOIN (VALUES
                    ('Member', 'tasks.create_for', 'Own'),
                    ('Department Admin', 'tasks.create_for', 'Own')
                ) AS g(role, permission, scope) ON g.role = r."Name"
                WHERE NOT r."IsBuiltIn"
                ON CONFLICT ("RoleId", "Permission") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous build doesn't know tasks.create_for, so every grant of it goes (custom roles' too). Requestees named on
            // tasks that aren't requests stay: the previous build shows them as "Requested for".
            migrationBuilder.Sql("""DELETE FROM "RolePermissions" WHERE "Permission" = 'tasks.create_for';""");

            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_AspNetUsers_RequesteeId",
                table: "Tasks");

            migrationBuilder.RenameColumn(
                name: "RequesteeId",
                table: "Tasks",
                newName: "RequestedForId");

            migrationBuilder.RenameIndex(
                name: "IX_Tasks_RequesteeId",
                table: "Tasks",
                newName: "IX_Tasks_RequestedForId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_AspNetUsers_RequestedForId",
                table: "Tasks",
                column: "RequestedForId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
