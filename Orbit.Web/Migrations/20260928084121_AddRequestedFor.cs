using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestedFor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RequestedForId",
                table: "Tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_RequestedForId",
                table: "Tasks",
                column: "RequestedForId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_AspNetUsers_RequestedForId",
                table: "Tasks",
                column: "RequestedForId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous build has no User question type and allows requests.submit only at Own (spec §6.20): a User question
            // becomes a Text one, and a Department grant goes back to Own. Who a request was for stays in its description.
            migrationBuilder.Sql("""UPDATE "RequestQuestions" SET "QuestionType" = 'Text' WHERE "QuestionType" = 'User';""");
            migrationBuilder.Sql("""UPDATE "RolePermissions" SET "Scope" = 'Own' WHERE "Permission" = 'requests.submit' AND "Scope" <> 'Own';""");

            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_AspNetUsers_RequestedForId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_RequestedForId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "RequestedForId",
                table: "Tasks");
        }
    }
}
