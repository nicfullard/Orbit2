using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddCriticalPathAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequiredBufferWorkingDays",
                table: "Projects",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CriticalPathAnalyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RunById = table.Column<Guid>(type: "uuid", nullable: true),
                    PlannedCompletionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NetworkCompletionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TargetDateAtRun = table.Column<DateOnly>(type: "date", nullable: true),
                    RequiredBufferAtRun = table.Column<int>(type: "integer", nullable: true),
                    BufferConsumedDays = table.Column<int>(type: "integer", nullable: true),
                    BufferRemainingDays = table.Column<int>(type: "integer", nullable: true),
                    BufferConsumptionPercent = table.Column<int>(type: "integer", nullable: true),
                    BufferStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CriticalTaskCount = table.Column<int>(type: "integer", nullable: false),
                    NearCriticalTaskCount = table.Column<int>(type: "integer", nullable: false),
                    CriticalPathCount = table.Column<int>(type: "integer", nullable: false),
                    WarningCount = table.Column<int>(type: "integer", nullable: false),
                    InputFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResultData = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CriticalPathAnalyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CriticalPathAnalyses_AspNetUsers_RunById",
                        column: x => x.RunById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CriticalPathAnalyses_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkingCalendarExceptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsWorking = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkingCalendarExceptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkingCalendars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MondayWorking = table.Column<bool>(type: "boolean", nullable: false),
                    TuesdayWorking = table.Column<bool>(type: "boolean", nullable: false),
                    WednesdayWorking = table.Column<bool>(type: "boolean", nullable: false),
                    ThursdayWorking = table.Column<bool>(type: "boolean", nullable: false),
                    FridayWorking = table.Column<bool>(type: "boolean", nullable: false),
                    SaturdayWorking = table.Column<bool>(type: "boolean", nullable: false),
                    SundayWorking = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkingCalendars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CriticalPathAnalyses_ProjectId_RunAt",
                table: "CriticalPathAnalyses",
                columns: new[] { "ProjectId", "RunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CriticalPathAnalyses_RunById",
                table: "CriticalPathAnalyses",
                column: "RunById");

            migrationBuilder.CreateIndex(
                name: "IX_WorkingCalendarExceptions_Date",
                table: "WorkingCalendarExceptions",
                column: "Date",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CriticalPathAnalyses");

            migrationBuilder.DropTable(
                name: "WorkingCalendarExceptions");

            migrationBuilder.DropTable(
                name: "WorkingCalendars");

            migrationBuilder.DropColumn(
                name: "RequiredBufferWorkingDays",
                table: "Projects");
        }
    }
}
