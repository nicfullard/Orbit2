using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Comments_OneParent",
                table: "Comments");

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "Comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Comments_ProjectId",
                table: "Comments",
                column: "ProjectId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Comments_OneParent",
                table: "Comments",
                sql: "num_nonnulls(\"TaskId\", \"ProjectId\", \"AssetId\") = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_Comments_Projects_ProjectId",
                table: "Comments",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Without the column a project's comments have nothing to belong to, and the task-or-asset constraint restored below
            // would refuse them.
            migrationBuilder.Sql("""DELETE FROM "Comments" WHERE "ProjectId" IS NOT NULL;""");

            migrationBuilder.DropForeignKey(
                name: "FK_Comments_Projects_ProjectId",
                table: "Comments");

            migrationBuilder.DropIndex(
                name: "IX_Comments_ProjectId",
                table: "Comments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Comments_OneParent",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "Comments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Comments_OneParent",
                table: "Comments",
                sql: "(\"TaskId\" IS NULL) <> (\"AssetId\" IS NULL)");
        }
    }
}
