using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class RequestUrgencyAndAssetTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Urgency and Asset type questions (spec §6.20, §13 item 68). The two field types are stored by name, so they need no
            // column; a task step gains the Urgency field its priority comes from, and an answer keeps the asset type it picked.
            migrationBuilder.AddColumn<Guid>(
                name: "AssetTypeId",
                table: "RequestFormAnswers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PriorityFieldId",
                table: "RequestFlowSteps",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestFormAnswers_AssetTypeId",
                table: "RequestFormAnswers",
                column: "AssetTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowSteps_PriorityFieldId",
                table: "RequestFlowSteps",
                column: "PriorityFieldId");

            migrationBuilder.AddForeignKey(
                name: "FK_RequestFlowSteps_RequestFormFields_PriorityFieldId",
                table: "RequestFlowSteps",
                column: "PriorityFieldId",
                principalTable: "RequestFormFields",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestFormAnswers_AssetTypes_AssetTypeId",
                table: "RequestFormAnswers",
                column: "AssetTypeId",
                principalTable: "AssetTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous build throws on a field type it doesn't know, so the two new types become the nearest old ones: an
            // Urgency a Choice of the four priorities (its answers already hold their names), an Asset type a Text (its answers hold
            // the type's name). Tasks already created keep the priority they were given.
            migrationBuilder.Sql("""UPDATE "RequestFormFields" SET "FieldType" = 'Choice', "Choices" = ARRAY['Low', 'Medium', 'High', 'Critical'], "PickerScope" = NULL WHERE "FieldType" = 'Urgency';""");
            migrationBuilder.Sql("""UPDATE "RequestFormFields" SET "FieldType" = 'Text', "PickerScope" = NULL WHERE "FieldType" = 'AssetType';""");

            migrationBuilder.DropForeignKey(
                name: "FK_RequestFlowSteps_RequestFormFields_PriorityFieldId",
                table: "RequestFlowSteps");

            migrationBuilder.DropForeignKey(
                name: "FK_RequestFormAnswers_AssetTypes_AssetTypeId",
                table: "RequestFormAnswers");

            migrationBuilder.DropIndex(
                name: "IX_RequestFormAnswers_AssetTypeId",
                table: "RequestFormAnswers");

            migrationBuilder.DropIndex(
                name: "IX_RequestFlowSteps_PriorityFieldId",
                table: "RequestFlowSteps");

            migrationBuilder.DropColumn(
                name: "AssetTypeId",
                table: "RequestFormAnswers");

            migrationBuilder.DropColumn(
                name: "PriorityFieldId",
                table: "RequestFlowSteps");
        }
    }
}
