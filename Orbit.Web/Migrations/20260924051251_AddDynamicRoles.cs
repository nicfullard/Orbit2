using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <summary>
    /// Roles become data (spec §6.5): AspNetRoles gains Description / IsBuiltIn / timestamps, RolePermissions holds each
    /// role's grants, and an API key points at a role row instead of carrying a role name. The data steps keep every
    /// existing user and key exactly as they were: SystemAdmin becomes the built-in "System Administrator" role,
    /// DepartmentAdmin becomes "Department Admin" and Member stays, the two migrated roles get the grants that reproduce
    /// their old rights, and each key's RoleId is filled in from its old Role name before that column goes.
    /// On an empty database (no roles yet) the data steps are no-ops and DbInitializer creates the roles.
    /// </summary>
    public partial class AddDynamicRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- schema: roles -----------------------------------------------------------------------
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "AspNetRoles",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "AspNetRoles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBuiltIn",
                table: "AspNetRoles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "AspNetRoles",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permission = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.Permission });
                    table.ForeignKey(
                        name: "FK_RolePermissions_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoles_SingleBuiltIn",
                table: "AspNetRoles",
                column: "IsBuiltIn",
                unique: true,
                filter: "\"IsBuiltIn\"");

            // --- data: the three fixed roles become rows with grants ----------------------------------
            migrationBuilder.Sql("""
                UPDATE "AspNetRoles"
                SET "Name" = 'System Administrator', "NormalizedName" = 'SYSTEM ADMINISTRATOR', "IsBuiltIn" = TRUE,
                    "Description" = 'Every permission, everywhere. Built in; cannot be edited or deleted.'
                WHERE "Name" = 'SystemAdmin';

                UPDATE "AspNetRoles"
                SET "Name" = 'Department Admin', "NormalizedName" = 'DEPARTMENT ADMIN',
                    "Description" = 'Manages every task and project in their own department, including colleagues'' time.'
                WHERE "Name" = 'DepartmentAdmin';

                UPDATE "AspNetRoles"
                SET "Description" = 'Sees their department''s work; edits, plans and logs time on their own tasks; takes unassigned tasks.'
                WHERE "Name" = 'Member';

                INSERT INTO "RolePermissions" ("RoleId", "Permission", "Scope")
                SELECT r."Id", g.permission, g.scope
                FROM "AspNetRoles" r
                CROSS JOIN (VALUES
                    ('tasks.view', 'Department'), ('tasks.create', 'Department'), ('tasks.edit', 'Own'), ('tasks.take', 'Department'),
                    ('tasks.plan', 'Department'), ('projects.view', 'Department'), ('projects.create', 'Department'),
                    ('projects.edit', 'Own'), ('time.log', 'Own')) AS g(permission, scope)
                WHERE r."Name" = 'Member';

                INSERT INTO "RolePermissions" ("RoleId", "Permission", "Scope")
                SELECT r."Id", g.permission, g.scope
                FROM "AspNetRoles" r
                CROSS JOIN (VALUES
                    ('tasks.view', 'Department'), ('tasks.create', 'Department'), ('tasks.edit', 'Department'), ('tasks.take', 'Department'),
                    ('tasks.plan', 'Department'), ('projects.view', 'Department'), ('projects.create', 'Department'),
                    ('projects.edit', 'Department'), ('time.log', 'Department')) AS g(permission, scope)
                WHERE r."Name" = 'Department Admin';
                """);

            // --- schema + data: API keys point at a role row ----------------------------------------
            migrationBuilder.AddColumn<Guid>(
                name: "RoleId",
                table: "ApiKeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "ApiKeys" k
                SET "RoleId" = r."Id"
                FROM "AspNetRoles" r
                WHERE r."Name" = CASE k."Role"
                    WHEN 'SystemAdmin' THEN 'System Administrator'
                    WHEN 'DepartmentAdmin' THEN 'Department Admin'
                    ELSE k."Role" END;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "RoleId",
                table: "ApiKeys",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_RoleId",
                table: "ApiKeys",
                column: "RoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_ApiKeys_AspNetRoles_RoleId",
                table: "ApiKeys",
                column: "RoleId",
                principalTable: "AspNetRoles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropColumn(
                name: "Role",
                table: "ApiKeys");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keys get their role name back from the role they point at; a custom role becomes Member.
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "ApiKeys",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Member");

            migrationBuilder.Sql("""
                UPDATE "ApiKeys" k
                SET "Role" = CASE
                    WHEN r."IsBuiltIn" THEN 'SystemAdmin'
                    WHEN r."Name" = 'Department Admin' THEN 'DepartmentAdmin'
                    ELSE 'Member' END
                FROM "AspNetRoles" r
                WHERE r."Id" = k."RoleId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_ApiKeys_AspNetRoles_RoleId",
                table: "ApiKeys");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_RoleId",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "RoleId",
                table: "ApiKeys");

            migrationBuilder.Sql("""
                UPDATE "AspNetRoles" SET "Name" = 'SystemAdmin', "NormalizedName" = 'SYSTEMADMIN' WHERE "IsBuiltIn";
                UPDATE "AspNetRoles" SET "Name" = 'DepartmentAdmin', "NormalizedName" = 'DEPARTMENTADMIN' WHERE "Name" = 'Department Admin';
                """);

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropIndex(
                name: "IX_AspNetRoles_SingleBuiltIn",
                table: "AspNetRoles");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "AspNetRoles");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "AspNetRoles");

            migrationBuilder.DropColumn(
                name: "IsBuiltIn",
                table: "AspNetRoles");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "AspNetRoles");
        }
    }
}
