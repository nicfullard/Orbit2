using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddNagiosMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NagiosInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PasswordProtected = table.Column<string>(type: "text", nullable: true),
                    ValidateCertificate = table.Column<bool>(type: "boolean", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CheckIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    HostThresholdMinutes = table.Column<int>(type: "integer", nullable: false),
                    ServiceThresholdMinutes = table.Column<int>(type: "integer", nullable: false),
                    RaiseHostDown = table.Column<bool>(type: "boolean", nullable: false),
                    RaiseHostUnreachable = table.Column<bool>(type: "boolean", nullable: false),
                    RaiseServiceCritical = table.Column<bool>(type: "boolean", nullable: false),
                    RaiseServiceWarning = table.Column<bool>(type: "boolean", nullable: false),
                    RaiseServiceUnknown = table.Column<bool>(type: "boolean", nullable: false),
                    SkipScheduledDowntime = table.Column<bool>(type: "boolean", nullable: false),
                    SkipAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    MaxNewTasksPerCheck = table.Column<int>(type: "integer", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskPriority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSucceededAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastQueryTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    HeldBack = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NagiosInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NagiosInstances_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_NagiosInstances_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NagiosIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NagiosInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    HostName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ServiceDescription = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Output = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ProblemSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClockFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastOkAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NagiosIncidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NagiosIncidents_NagiosInstances_NagiosInstanceId",
                        column: x => x.NagiosInstanceId,
                        principalTable: "NagiosInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NagiosIncidents_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NagiosInstanceAssignees",
                columns: table => new
                {
                    NagiosInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NagiosInstanceAssignees", x => new { x.NagiosInstanceId, x.UserId });
                    table.ForeignKey(
                        name: "FK_NagiosInstanceAssignees_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NagiosInstanceAssignees_NagiosInstances_NagiosInstanceId",
                        column: x => x.NagiosInstanceId,
                        principalTable: "NagiosInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NagiosIncidents_NagiosInstanceId_ResolvedAt",
                table: "NagiosIncidents",
                columns: new[] { "NagiosInstanceId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NagiosIncidents_Open",
                table: "NagiosIncidents",
                columns: new[] { "NagiosInstanceId", "HostName", "ServiceDescription" },
                unique: true,
                filter: "\"ResolvedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NagiosIncidents_TaskId",
                table: "NagiosIncidents",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_NagiosInstanceAssignees_UserId",
                table: "NagiosInstanceAssignees",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_NagiosInstances_AgentId",
                table: "NagiosInstances",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_NagiosInstances_DepartmentId",
                table: "NagiosInstances",
                column: "DepartmentId");

            // An instance's name is unique ignoring case (spec §6.21). An expression index, which EF can't express; the service
            // checks the same rule first. nagios.manage is granted to nobody by default, so there is no grant step.
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_NagiosInstances_Name_Lower" ON "NagiosInstances" (lower("Name"));""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous build throws on a task source it doesn't know and has no such permission: the tasks Nagios raised stay,
            // as ordinary manual tasks (their descriptions still say where they came from), and the grants go. The incidents and
            // the instances' stored passwords go with their tables.
            migrationBuilder.Sql("""UPDATE "Tasks" SET "Source" = 'Manual' WHERE "Source" = 'Nagios';""");
            migrationBuilder.Sql("""DELETE FROM "RolePermissions" WHERE "Permission" = 'nagios.manage';""");

            migrationBuilder.DropTable(
                name: "NagiosIncidents");

            migrationBuilder.DropTable(
                name: "NagiosInstanceAssignees");

            migrationBuilder.DropTable(
                name: "NagiosInstances");
        }
    }
}
