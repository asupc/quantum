using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quantum.Migrations.SqliteMigrations
{
    /// <inheritdoc />
    public partial class ExternalPushBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayTitle",
                table: "t_chat_session",
                type: "nvarchar(200)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionTitle",
                table: "t_chat_message",
                type: "nvarchar(200)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "t_external_push_credential",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SecretHash = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RateLimitPerMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    DailyQuota = table.Column<int>(type: "INTEGER", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: true),
                    TotalSent = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_external_push_credential", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_external_push_request",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    CredentialId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", nullable: true),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    NotificationId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    MsgId = table.Column<string>(type: "TEXT", nullable: true),
                    SessionKey = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadBytes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_external_push_request", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_t_external_push_credential_Enabled",
                table: "t_external_push_credential",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_t_external_push_request_CredentialId_CreatedAtUtc",
                table: "t_external_push_request",
                columns: new[] { "CredentialId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_t_external_push_request_CredentialId_IdempotencyKey",
                table: "t_external_push_request",
                columns: new[] { "CredentialId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "t_external_push_credential");

            migrationBuilder.DropTable(
                name: "t_external_push_request");

            migrationBuilder.DropColumn(
                name: "DisplayTitle",
                table: "t_chat_session");

            migrationBuilder.DropColumn(
                name: "SessionTitle",
                table: "t_chat_message");
        }
    }
}
