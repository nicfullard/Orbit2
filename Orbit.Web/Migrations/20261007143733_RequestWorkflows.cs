using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class RequestWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Requests become flows of steps (spec §6.20, §13 item 67). The option/question catalogue was never used, so it is dropped
            // rather than carried over - its lower("Title") index goes with the table. Categories stay. Tasks already logged with
            // Source = Request stay as they are: they hold their questions and answers as text.
            migrationBuilder.DropTable(
                name: "RequestQuestions");

            migrationBuilder.DropTable(
                name: "RequestOptions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Attachments_OneParent",
                table: "Attachments");

            migrationBuilder.AddColumn<Guid>(
                name: "RequestId",
                table: "Attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RequestActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RunsOn = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Script = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestActions_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RequestFlows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestFlows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestFlows_RequestCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "RequestCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestActionParameters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Label = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestActionParameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestActionParameters_RequestActions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "RequestActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FlowId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Requests_AspNetUsers_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Requests_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Requests_RequestFlows_FlowId",
                        column: x => x.FlowId,
                        principalTable: "RequestFlows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RequestApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StepId = table.Column<Guid>(type: "uuid", nullable: false),
                    StageOrder = table.Column<int>(type: "integer", nullable: false),
                    ApproverId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestApprovals_AspNetUsers_ApproverId",
                        column: x => x.ApproverId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RequestFlowApprovalStages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StepId = table.Column<Guid>(type: "uuid", nullable: false),
                    StageOrder = table.Column<int>(type: "integer", nullable: false),
                    Rule = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestFlowApprovalStages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequestFlowApprovers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestFlowApprovers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestFlowApprovers_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestFlowApprovers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestFlowApprovers_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestFlowApprovers_RequestFlowApprovalStages_StageId",
                        column: x => x.StageId,
                        principalTable: "RequestFlowApprovalStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestFlowStepActionInputs",
                columns: table => new
                {
                    StepId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParameterKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ValueTemplate = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestFlowStepActionInputs", x => new { x.StepId, x.ParameterKey });
                });

            migrationBuilder.CreateTable(
                name: "RequestFlowStepAssignees",
                columns: table => new
                {
                    StepId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestFlowStepAssignees", x => new { x.StepId, x.UserId });
                    table.ForeignKey(
                        name: "FK_RequestFlowStepAssignees_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RequestFlowStepDependencies",
                columns: table => new
                {
                    StepId = table.Column<Guid>(type: "uuid", nullable: false),
                    DependsOnStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequiredOutcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestFlowStepDependencies", x => new { x.StepId, x.DependsOnStepId });
                });

            migrationBuilder.CreateTable(
                name: "RequestFlowSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    PerformedById = table.Column<Guid>(type: "uuid", nullable: true),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TaskDepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    TaskType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TaskPriority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TitleTemplate = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DescriptionTemplate = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DueDateFieldId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetFieldId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectFieldId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequesteeSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequesteeFieldId = table.Column<Guid>(type: "uuid", nullable: true),
                    CopyAllAttachments = table.Column<bool>(type: "boolean", nullable: false),
                    CopyAttachmentsFromStepId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestFlowSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestFlowSteps_AspNetUsers_PerformedById",
                        column: x => x.PerformedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestFlowSteps_Departments_TaskDepartmentId",
                        column: x => x.TaskDepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestFlowSteps_RequestActions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "RequestActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestFlowSteps_RequestFlowSteps_CopyAttachmentsFromStepId",
                        column: x => x.CopyAttachmentsFromStepId,
                        principalTable: "RequestFlowSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RequestFlowSteps_RequestFlows_FlowId",
                        column: x => x.FlowId,
                        principalTable: "RequestFlows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestFormFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StepId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Prompt = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    HelpText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FieldType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    Choices = table.Column<List<string>>(type: "text[]", nullable: false),
                    PickerScope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestFormFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestFormFields_RequestFlowSteps_StepId",
                        column: x => x.StepId,
                        principalTable: "RequestFlowSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AssignedToId = table.Column<Guid>(type: "uuid", nullable: true),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedById = table.Column<Guid>(type: "uuid", nullable: true),
                    Output = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestSteps_AspNetUsers_AssignedToId",
                        column: x => x.AssignedToId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestSteps_AspNetUsers_CompletedById",
                        column: x => x.CompletedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestSteps_RequestFlowSteps_FlowStepId",
                        column: x => x.FlowStepId,
                        principalTable: "RequestFlowSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestSteps_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RequestSteps_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RequestFormAnswers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StepId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestFormAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestFormAnswers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestFormAnswers_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RequestFormAnswers_Attachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "Attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RequestFormAnswers_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RequestFormAnswers_RequestFormFields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "RequestFormFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestFormAnswers_RequestSteps_StepId",
                        column: x => x.StepId,
                        principalTable: "RequestSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_RequestId",
                table: "Attachments",
                column: "RequestId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Attachments_OneParent",
                table: "Attachments",
                sql: "num_nonnulls(\"TaskId\", \"ProjectId\", \"AssetId\", \"RequestId\") = 1");

            migrationBuilder.CreateIndex(
                name: "IX_RequestActionParameters_ActionId_Key",
                table: "RequestActionParameters",
                columns: new[] { "ActionId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestActions_AgentId",
                table: "RequestActions",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestApprovals_ApproverId_Decision",
                table: "RequestApprovals",
                columns: new[] { "ApproverId", "Decision" });

            migrationBuilder.CreateIndex(
                name: "IX_RequestApprovals_StepId_StageOrder_ApproverId",
                table: "RequestApprovals",
                columns: new[] { "StepId", "StageOrder", "ApproverId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowApprovalStages_StepId",
                table: "RequestFlowApprovalStages",
                column: "StepId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowApprovers_DepartmentId",
                table: "RequestFlowApprovers",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowApprovers_RoleId",
                table: "RequestFlowApprovers",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowApprovers_StageId",
                table: "RequestFlowApprovers",
                column: "StageId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowApprovers_UserId",
                table: "RequestFlowApprovers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlows_CategoryId",
                table: "RequestFlows",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowStepAssignees_UserId",
                table: "RequestFlowStepAssignees",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowStepDependencies_DependsOnStepId",
                table: "RequestFlowStepDependencies",
                column: "DependsOnStepId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowSteps_ActionId",
                table: "RequestFlowSteps",
                column: "ActionId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowSteps_AssetFieldId",
                table: "RequestFlowSteps",
                column: "AssetFieldId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowSteps_CopyAttachmentsFromStepId",
                table: "RequestFlowSteps",
                column: "CopyAttachmentsFromStepId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowSteps_DueDateFieldId",
                table: "RequestFlowSteps",
                column: "DueDateFieldId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowSteps_FlowId_Key",
                table: "RequestFlowSteps",
                columns: new[] { "FlowId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowSteps_PerformedById",
                table: "RequestFlowSteps",
                column: "PerformedById");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowSteps_ProjectFieldId",
                table: "RequestFlowSteps",
                column: "ProjectFieldId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowSteps_RequesteeFieldId",
                table: "RequestFlowSteps",
                column: "RequesteeFieldId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFlowSteps_TaskDepartmentId",
                table: "RequestFlowSteps",
                column: "TaskDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFormAnswers_AssetId",
                table: "RequestFormAnswers",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFormAnswers_AttachmentId",
                table: "RequestFormAnswers",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFormAnswers_FieldId",
                table: "RequestFormAnswers",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFormAnswers_ProjectId",
                table: "RequestFormAnswers",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFormAnswers_StepId_FieldId",
                table: "RequestFormAnswers",
                columns: new[] { "StepId", "FieldId" });

            migrationBuilder.CreateIndex(
                name: "IX_RequestFormAnswers_UserId",
                table: "RequestFormAnswers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestFormFields_StepId_Key",
                table: "RequestFormFields",
                columns: new[] { "StepId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requests_DepartmentId_Status",
                table: "Requests",
                columns: new[] { "DepartmentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_FlowId",
                table: "Requests",
                column: "FlowId");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_IdempotencyKey",
                table: "Requests",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_Number",
                table: "Requests",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requests_RequesterId",
                table: "Requests",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestSteps_AssignedToId",
                table: "RequestSteps",
                column: "AssignedToId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestSteps_CompletedById",
                table: "RequestSteps",
                column: "CompletedById");

            migrationBuilder.CreateIndex(
                name: "IX_RequestSteps_FlowStepId",
                table: "RequestSteps",
                column: "FlowStepId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestSteps_RequestId_FlowStepId",
                table: "RequestSteps",
                columns: new[] { "RequestId", "FlowStepId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestSteps_Status",
                table: "RequestSteps",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RequestSteps_TaskId",
                table: "RequestSteps",
                column: "TaskId");

            migrationBuilder.AddForeignKey(
                name: "FK_Attachments_Requests_RequestId",
                table: "Attachments",
                column: "RequestId",
                principalTable: "Requests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestApprovals_RequestSteps_StepId",
                table: "RequestApprovals",
                column: "StepId",
                principalTable: "RequestSteps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestFlowApprovalStages_RequestFlowSteps_StepId",
                table: "RequestFlowApprovalStages",
                column: "StepId",
                principalTable: "RequestFlowSteps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestFlowStepActionInputs_RequestFlowSteps_StepId",
                table: "RequestFlowStepActionInputs",
                column: "StepId",
                principalTable: "RequestFlowSteps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestFlowStepAssignees_RequestFlowSteps_StepId",
                table: "RequestFlowStepAssignees",
                column: "StepId",
                principalTable: "RequestFlowSteps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestFlowStepDependencies_RequestFlowSteps_DependsOnStepId",
                table: "RequestFlowStepDependencies",
                column: "DependsOnStepId",
                principalTable: "RequestFlowSteps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestFlowStepDependencies_RequestFlowSteps_StepId",
                table: "RequestFlowStepDependencies",
                column: "StepId",
                principalTable: "RequestFlowSteps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestFlowSteps_RequestFormFields_AssetFieldId",
                table: "RequestFlowSteps",
                column: "AssetFieldId",
                principalTable: "RequestFormFields",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestFlowSteps_RequestFormFields_DueDateFieldId",
                table: "RequestFlowSteps",
                column: "DueDateFieldId",
                principalTable: "RequestFormFields",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestFlowSteps_RequestFormFields_ProjectFieldId",
                table: "RequestFlowSteps",
                column: "ProjectFieldId",
                principalTable: "RequestFormFields",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestFlowSteps_RequestFormFields_RequesteeFieldId",
                table: "RequestFlowSteps",
                column: "RequesteeFieldId",
                principalTable: "RequestFormFields",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Titles are unique ignoring case (spec §6.20): a flow's within its category, an action's everywhere. Expression indexes,
            // which EF can't express; the services check the same rule first.
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_RequestFlows_CategoryId_Title_Lower" ON "RequestFlows" ("CategoryId", lower("Title"));""");
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_RequestActions_Name_Lower" ON "RequestActions" (lower("Name"));""");

            // The shipped roles' default grants (spec §6.5), for a database whose roles already exist - a new database gets them from
            // DbInitializer when it creates the roles. A shipped role that has been renamed or deleted is left alone, and the built-in
            // role needs no rows: it resolves to every permission at All. actions.create is granted to nobody by default.
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleId", "Permission", "Scope")
                SELECT r."Id", g.permission, g.scope
                FROM "AspNetRoles" r
                JOIN (VALUES
                    ('Department Admin', 'requests.manage', 'Department')
                ) AS g(role, permission, scope) ON g.role = r."Name"
                WHERE NOT r."IsBuiltIn"
                ON CONFLICT ("RoleId", "Permission") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous build has no request parent for files and no such permissions: a request's files go (the three-parent
            // constraint below would refuse them), and the grants name permissions that no longer exist. The tasks the flows created
            // stay, as ordinary request-sourced tasks.
            migrationBuilder.Sql("""DELETE FROM "Attachments" WHERE "RequestId" IS NOT NULL;""");
            migrationBuilder.Sql("""DELETE FROM "RolePermissions" WHERE "Permission" IN ('requests.manage', 'actions.create');""");

            migrationBuilder.DropForeignKey(
                name: "FK_Attachments_Requests_RequestId",
                table: "Attachments");

            migrationBuilder.DropForeignKey(
                name: "FK_RequestFlowSteps_RequestActions_ActionId",
                table: "RequestFlowSteps");

            migrationBuilder.DropForeignKey(
                name: "FK_RequestFormFields_RequestFlowSteps_StepId",
                table: "RequestFormFields");

            migrationBuilder.DropTable(
                name: "RequestActionParameters");

            migrationBuilder.DropTable(
                name: "RequestApprovals");

            migrationBuilder.DropTable(
                name: "RequestFlowApprovers");

            migrationBuilder.DropTable(
                name: "RequestFlowStepActionInputs");

            migrationBuilder.DropTable(
                name: "RequestFlowStepAssignees");

            migrationBuilder.DropTable(
                name: "RequestFlowStepDependencies");

            migrationBuilder.DropTable(
                name: "RequestFormAnswers");

            migrationBuilder.DropTable(
                name: "RequestFlowApprovalStages");

            migrationBuilder.DropTable(
                name: "RequestSteps");

            migrationBuilder.DropTable(
                name: "Requests");

            migrationBuilder.DropTable(
                name: "RequestActions");

            migrationBuilder.DropTable(
                name: "RequestFlowSteps");

            migrationBuilder.DropTable(
                name: "RequestFlows");

            migrationBuilder.DropTable(
                name: "RequestFormFields");

            migrationBuilder.DropIndex(
                name: "IX_Attachments_RequestId",
                table: "Attachments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Attachments_OneParent",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "Attachments");

            migrationBuilder.CreateTable(
                name: "RequestOptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowAttachments = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TaskType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
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
                    Choices = table.Column<List<string>>(type: "text[]", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    HelpText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    Prompt = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    QuestionType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SetsDueDate = table.Column<bool>(type: "boolean", nullable: false)
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

            migrationBuilder.AddCheckConstraint(
                name: "CK_Attachments_OneParent",
                table: "Attachments",
                sql: "num_nonnulls(\"TaskId\", \"ProjectId\", \"AssetId\") = 1");

            migrationBuilder.CreateIndex(
                name: "IX_RequestOptions_CategoryId",
                table: "RequestOptions",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestQuestions_OptionId",
                table: "RequestQuestions",
                column: "OptionId");

            // The option titles' case-insensitive uniqueness, as AddRequests created it.
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_RequestOptions_CategoryId_Title_Lower" ON "RequestOptions" ("CategoryId", lower("Title"));""");
        }
    }
}
