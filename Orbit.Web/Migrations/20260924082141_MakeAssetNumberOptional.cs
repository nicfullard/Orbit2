using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class MakeAssetNumberOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The asset number is the asset's number in the ERP asset register (spec §6.19), which not every asset has. The unique
            // lower("AssetNumber") index from AddAssets stays: it allows any number of assets without one.
            migrationBuilder.AlterColumn<string>(
                name: "AssetNumber",
                table: "Assets",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "Assets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assets_IdempotencyKey",
                table: "Assets",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Assets_IdempotencyKey",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "Assets");

            // The number was required before: give assets without one a unique placeholder (NONE- and the id, 37 characters),
            // since "" for all of them would clash on the unique lower("AssetNumber") index.
            migrationBuilder.Sql("""UPDATE "Assets" SET "AssetNumber" = 'NONE-' || replace("Id"::text, '-', '') WHERE "AssetNumber" IS NULL;""");

            migrationBuilder.AlterColumn<string>(
                name: "AssetNumber",
                table: "Assets",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50,
                oldNullable: true);
        }
    }
}
