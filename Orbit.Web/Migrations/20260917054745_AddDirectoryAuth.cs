using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectoryAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited: the scaffolded default was "". The enum is stored as its name, so existing users must be
            // backfilled with "Local" or they could no longer be read at all.
            migrationBuilder.AddColumn<string>(
                name: "AuthSource",
                table: "AspNetUsers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Local");

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RegistrationTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RegistrationExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HashedSecret = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SecretPrefix = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    MachineName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OsDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    LastConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastIpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    RegisteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LdapSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Server = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    UseSsl = table.Column<bool>(type: "boolean", nullable: false),
                    ValidateCertificate = table.Column<bool>(type: "boolean", nullable: false),
                    BindDn = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BindPasswordProtected = table.Column<string>(type: "text", nullable: true),
                    SearchBase = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UserFilter = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LdapSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_HashedSecret",
                table: "Agents",
                column: "HashedSecret",
                unique: true,
                filter: "\"HashedSecret\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_RegistrationTokenHash",
                table: "Agents",
                column: "RegistrationTokenHash",
                unique: true,
                filter: "\"RegistrationTokenHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "LdapSettings");

            migrationBuilder.DropColumn(
                name: "AuthSource",
                table: "AspNetUsers");
        }
    }
}
