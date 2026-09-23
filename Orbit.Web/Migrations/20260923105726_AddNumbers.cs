using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Number",
                table: "Tasks",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Number",
                table: "Projects",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "NumberCounters",
                columns: table => new
                {
                    Prefix = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Last = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberCounters", x => new { x.Prefix, x.Year });
                });

            // Existing rows get numbers in creation order within their year (spec §5.1), and each year's counter
            // starts where that backfill ends, so the first record created after the upgrade continues the sequence.
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT "Id", 'T-' || to_char("CreatedAt", 'YY') || '-' ||
                           lpad((row_number() OVER (PARTITION BY date_part('year', "CreatedAt") ORDER BY "CreatedAt", "Id"))::text, 5, '0') AS n
                    FROM "Tasks")
                UPDATE "Tasks" AS t SET "Number" = numbered.n FROM numbered WHERE t."Id" = numbered."Id";

                WITH numbered AS (
                    SELECT "Id", 'P-' || to_char("CreatedAt", 'YY') || '-' ||
                           lpad((row_number() OVER (PARTITION BY date_part('year', "CreatedAt") ORDER BY "CreatedAt", "Id"))::text, 5, '0') AS n
                    FROM "Projects")
                UPDATE "Projects" AS p SET "Number" = numbered.n FROM numbered WHERE p."Id" = numbered."Id";

                INSERT INTO "NumberCounters" ("Prefix", "Year", "Last")
                SELECT 'T', date_part('year', "CreatedAt")::int, count(*) FROM "Tasks" GROUP BY 2;

                INSERT INTO "NumberCounters" ("Prefix", "Year", "Last")
                SELECT 'P', date_part('year', "CreatedAt")::int, count(*) FROM "Projects" GROUP BY 2;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Number",
                table: "Tasks",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Number",
                table: "Projects",
                column: "Number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NumberCounters");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_Number",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Projects_Number",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "Projects");
        }
    }
}
