using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class MoveAttachmentContentIntoDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StoragePath",
                table: "Attachments");

            migrationBuilder.CreateTable(
                name: "AttachmentContents",
                columns: table => new
                {
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttachmentContents", x => x.AttachmentId);
                    table.ForeignKey(
                        name: "FK_AttachmentContents_Attachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "Attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttachmentContents");

            migrationBuilder.AddColumn<string>(
                name: "StoragePath",
                table: "Attachments",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");
        }
    }
}
