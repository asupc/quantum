using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quantum.Migrations.MySqlMigrations
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
                    DisplayName = table.Column<string>(type: "longtext", nullable: true),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SecretHash = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RateLimitPerMinute = table.Column<int>(type: "int", nullable: false),
                    DailyQuota = table.Column<int>(type: "int", nullable: false),
                    Remark = table.Column<string>(type: "longtext", nullable: true),
                    TotalSent = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_external_push_credential", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "t_external_push_request",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    CredentialId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "varchar(255)", nullable: true),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    NotificationId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    MsgId = table.Column<string>(type: "longtext", nullable: true),
                    SessionKey = table.Column<string>(type: "longtext", nullable: true),
                    PayloadBytes = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_external_push_request", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

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
