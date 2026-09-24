using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Attachments_OneParent",
                table: "Attachments");

            migrationBuilder.AlterColumn<Guid>(
                name: "TaskId",
                table: "Comments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "AssetId",
                table: "Comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssetId",
                table: "Attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssetLocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetLocations_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CheckIntervalDays = table.Column<int>(type: "integer", nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetTypes_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AssetTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Manufacturer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SerialNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AssetLocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurchaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PurchaseValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PurchaseOrder = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InvoiceNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Supplier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WarrantyExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DisposedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    LastCheckedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    LastCheckOutcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assets_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assets_AssetLocations_AssetLocationId",
                        column: x => x.AssetLocationId,
                        principalTable: "AssetLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assets_AssetTypes_AssetTypeId",
                        column: x => x.AssetTypeId,
                        principalTable: "AssetTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assets_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetTypeProperties",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PropertyType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Options = table.Column<List<string>>(type: "text[]", nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetTypeProperties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetTypeProperties_AssetTypes_AssetTypeId",
                        column: x => x.AssetTypeId,
                        principalTable: "AssetTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssetAssignments",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AssignedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetAssignments", x => new { x.AssetId, x.UserId });
                    table.ForeignKey(
                        name: "FK_AssetAssignments_AspNetUsers_AssignedById",
                        column: x => x.AssignedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetAssignments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetAssignments_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssetChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CheckedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetChecks_AspNetUsers_CheckedById",
                        column: x => x.CheckedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetChecks_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssetPropertyValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetTypePropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetPropertyValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetPropertyValues_AssetTypeProperties_AssetTypePropertyId",
                        column: x => x.AssetTypePropertyId,
                        principalTable: "AssetTypeProperties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssetPropertyValues_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_AssetId",
                table: "Comments",
                column: "AssetId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Comments_OneParent",
                table: "Comments",
                sql: "(\"TaskId\" IS NULL) <> (\"AssetId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_AssetId",
                table: "Attachments",
                column: "AssetId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Attachments_OneParent",
                table: "Attachments",
                sql: "num_nonnulls(\"TaskId\", \"ProjectId\", \"AssetId\") = 1");

            migrationBuilder.CreateIndex(
                name: "IX_AssetAssignments_AssignedById",
                table: "AssetAssignments",
                column: "AssignedById");

            migrationBuilder.CreateIndex(
                name: "IX_AssetAssignments_UserId",
                table: "AssetAssignments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetChecks_AssetId_CheckDate",
                table: "AssetChecks",
                columns: new[] { "AssetId", "CheckDate" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetChecks_CheckedById",
                table: "AssetChecks",
                column: "CheckedById");

            migrationBuilder.CreateIndex(
                name: "IX_AssetLocations_DepartmentId",
                table: "AssetLocations",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetPropertyValues_AssetId_AssetTypePropertyId",
                table: "AssetPropertyValues",
                columns: new[] { "AssetId", "AssetTypePropertyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetPropertyValues_AssetTypePropertyId",
                table: "AssetPropertyValues",
                column: "AssetTypePropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_AssetLocationId",
                table: "Assets",
                column: "AssetLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_AssetTypeId",
                table: "Assets",
                column: "AssetTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_CreatedById",
                table: "Assets",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_DepartmentId",
                table: "Assets",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_LastCheckedOn",
                table: "Assets",
                column: "LastCheckedOn");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Status",
                table: "Assets",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_WarrantyExpiresOn",
                table: "Assets",
                column: "WarrantyExpiresOn");

            migrationBuilder.CreateIndex(
                name: "IX_AssetTypeProperties_AssetTypeId",
                table: "AssetTypeProperties",
                column: "AssetTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetTypes_DepartmentId",
                table: "AssetTypes",
                column: "DepartmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_Attachments_Assets_AssetId",
                table: "Attachments",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Comments_Assets_AssetId",
                table: "Comments",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // Case-insensitive uniqueness (spec §6.19) needs expression indexes, which EF can't model: the asset number across the
            // register, and a type's or location's name within its department. The services check the same rules first; these are
            // the backstop. lower("SerialNumber") serves the possible-duplicate flag.
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_Assets_AssetNumber_Lower" ON "Assets" (lower("AssetNumber"));""");
            migrationBuilder.Sql("""CREATE INDEX "IX_Assets_SerialNumber_Lower" ON "Assets" (lower("SerialNumber")) WHERE "SerialNumber" IS NOT NULL;""");
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_AssetTypes_DepartmentId_Name_Lower" ON "AssetTypes" ("DepartmentId", lower("Name"));""");
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_AssetLocations_DepartmentId_Name_Lower" ON "AssetLocations" ("DepartmentId", lower("Name"));""");

            // The shipped roles' default asset grants (spec §6.5), for a database whose roles already exist - a new database gets
            // them from DbInitializer when it creates the roles. A shipped role that has been renamed or deleted is left alone, and
            // the built-in role needs no rows: it resolves to every permission at All.
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleId", "Permission", "Scope")
                SELECT r."Id", g.permission, g.scope
                FROM "AspNetRoles" r
                JOIN (VALUES
                    ('Member', 'assets.view', 'Own'),
                    ('Member', 'assets.check', 'Own'),
                    ('Department Admin', 'assets.view', 'Department'),
                    ('Department Admin', 'assets.create', 'Department'),
                    ('Department Admin', 'assets.edit', 'Department'),
                    ('Department Admin', 'assets.check', 'Department'),
                    ('Department Admin', 'assets.configure', 'Department')
                ) AS g(role, permission, scope) ON g.role = r."Name"
                WHERE NOT r."IsBuiltIn"
                ON CONFLICT ("RoleId", "Permission") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Without the register, comments and files on assets have nothing to belong to (a file's bytes go with it by cascade),
            // and the asset grants name permissions that no longer exist. The expression indexes go with their tables.
            migrationBuilder.Sql("""DELETE FROM "Comments" WHERE "AssetId" IS NOT NULL;""");
            migrationBuilder.Sql("""DELETE FROM "Attachments" WHERE "AssetId" IS NOT NULL;""");
            migrationBuilder.Sql("""DELETE FROM "RolePermissions" WHERE "Permission" LIKE 'assets.%';""");

            migrationBuilder.DropForeignKey(
                name: "FK_Attachments_Assets_AssetId",
                table: "Attachments");

            migrationBuilder.DropForeignKey(
                name: "FK_Comments_Assets_AssetId",
                table: "Comments");

            migrationBuilder.DropTable(
                name: "AssetAssignments");

            migrationBuilder.DropTable(
                name: "AssetChecks");

            migrationBuilder.DropTable(
                name: "AssetPropertyValues");

            migrationBuilder.DropTable(
                name: "AssetTypeProperties");

            migrationBuilder.DropTable(
                name: "Assets");

            migrationBuilder.DropTable(
                name: "AssetLocations");

            migrationBuilder.DropTable(
                name: "AssetTypes");

            migrationBuilder.DropIndex(
                name: "IX_Comments_AssetId",
                table: "Comments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Comments_OneParent",
                table: "Comments");

            migrationBuilder.DropIndex(
                name: "IX_Attachments_AssetId",
                table: "Attachments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Attachments_OneParent",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "AssetId",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "AssetId",
                table: "Attachments");

            migrationBuilder.AlterColumn<Guid>(
                name: "TaskId",
                table: "Comments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Attachments_OneParent",
                table: "Attachments",
                sql: "(\"TaskId\" IS NULL) <> (\"ProjectId\" IS NULL)");
        }
    }
}
