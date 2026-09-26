using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class LinkTasksToAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssetId",
                table: "Tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssetId",
                table: "RecurringTaskDefinitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_AssetId",
                table: "Tasks",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringTaskDefinitions_AssetId",
                table: "RecurringTaskDefinitions",
                column: "AssetId");

            migrationBuilder.AddForeignKey(
                name: "FK_RecurringTaskDefinitions_Assets_AssetId",
                table: "RecurringTaskDefinitions",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_Assets_AssetId",
                table: "Tasks",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RecurringTaskDefinitions_Assets_AssetId",
                table: "RecurringTaskDefinitions");

            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_Assets_AssetId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_AssetId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_RecurringTaskDefinitions_AssetId",
                table: "RecurringTaskDefinitions");

            migrationBuilder.DropColumn(
                name: "AssetId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "AssetId",
                table: "RecurringTaskDefinitions");
        }
    }
}
