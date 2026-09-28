using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequestCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Icon = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Colour = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestCategories_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RequestOptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AllowAttachments = table.Column<bool>(type: "boolean", nullable: false),
                    TaskType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestOptions_RequestCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "RequestCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Prompt = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    HelpText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    QuestionType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    Choices = table.Column<List<string>>(type: "text[]", nullable: false),
                    SetsDueDate = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestQuestions_RequestOptions_OptionId",
                        column: x => x.OptionId,
                        principalTable: "RequestOptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestCategories_DepartmentId",
                table: "RequestCategories",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestOptions_CategoryId",
                table: "RequestOptions",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestQuestions_OptionId",
                table: "RequestQuestions",
                column: "OptionId");

            // Titles are unique ignoring case (spec §6.20): a category's within its department, an option's within its category.
            // Expression indexes, which EF can't express; RequestCatalogueService checks the same rule first.
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_RequestCategories_DepartmentId_Title_Lower" ON "RequestCategories" ("DepartmentId", lower("Title"));""");
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_RequestOptions_CategoryId_Title_Lower" ON "RequestOptions" ("CategoryId", lower("Title"));""");

            // The shipped roles' default request grants (spec §6.5), for a database whose roles already exist - a new database gets
            // them from DbInitializer when it creates the roles. A shipped role that has been renamed or deleted is left alone, and
            // the built-in role needs no rows: it resolves to every permission at All.
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleId", "Permission", "Scope")
                SELECT r."Id", g.permission, g.scope
                FROM "AspNetRoles" r
                JOIN (VALUES
                    ('Member', 'requests.submit', 'Own'),
                    ('Department Admin', 'requests.submit', 'Own'),
                    ('Department Admin', 'requests.configure', 'Department')
                ) AS g(role, permission, scope) ON g.role = r."Name"
                WHERE NOT r."IsBuiltIn"
                ON CONFLICT ("RoleId", "Permission") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous build has no Request source, so the tasks logged through the flows become ordinary manual tasks (their
            // questions and answers are in their descriptions). The request grants name permissions that no longer exist. The
            // expression indexes go with their tables.
            migrationBuilder.Sql("""UPDATE "Tasks" SET "Source" = 'Manual' WHERE "Source" = 'Request';""");
            migrationBuilder.Sql("""DELETE FROM "RolePermissions" WHERE "Permission" LIKE 'requests.%';""");

            migrationBuilder.DropTable(
                name: "RequestQuestions");

            migrationBuilder.DropTable(
                name: "RequestOptions");

            migrationBuilder.DropTable(
                name: "RequestCategories");
        }
    }
}
