using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quantum.Migrations.SqliteMigrations
{
    /// <inheritdoc />
    public partial class ChannelCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "t_channel_account",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(16)", nullable: true),
                    BotId = table.Column<string>(type: "nvarchar(128)", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CredentialCiphertext = table.Column<string>(type: "TEXT", nullable: true),
                    BindingVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChallengeHash = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    ChallengeExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ChallengeAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CandidatePeerId = table.Column<string>(type: "nvarchar(128)", nullable: true),
                    CandidateAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastErrorCode = table.Column<string>(type: "nvarchar(128)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_channel_account", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_channel_binding",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(16)", nullable: true),
                    AccountId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    PeerId = table.Column<string>(type: "nvarchar(128)", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    VerifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_channel_binding", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_channel_cursor",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    QqSessionId = table.Column<string>(type: "nvarchar(128)", nullable: true),
                    QqSequence = table.Column<long>(type: "INTEGER", nullable: true),
                    WeixinCursorCiphertext = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_channel_cursor", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_channel_inbox",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(32)", nullable: true),
                    MessageId = table.Column<string>(type: "nvarchar(191)", nullable: true),
                    MessageIndex = table.Column<string>(type: "nvarchar(160)", nullable: true),
                    PeerId = table.Column<string>(type: "nvarchar(128)", nullable: true),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(24)", nullable: true),
                    RejectReason = table.Column<string>(type: "nvarchar(80)", nullable: true),
                    ReplyRouteId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClaimedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_channel_inbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_channel_outbox",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    ReplyRouteId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    PeerId = table.Column<string>(type: "nvarchar(128)", nullable: true),
                    BindingVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    MessageSequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(32)", nullable: true),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(24)", nullable: true),
                    ClientId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClaimedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastErrorCode = table.Column<string>(type: "nvarchar(128)", nullable: true),
                    PlatformMessageId = table.Column<string>(type: "nvarchar(191)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_channel_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_channel_reply_route",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    PeerId = table.Column<string>(type: "nvarchar(128)", nullable: true),
                    BindingVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ReplyMessageId = table.Column<string>(type: "nvarchar(191)", nullable: true),
                    NextMessageSequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ContextTokenCiphertext = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_channel_reply_route", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_t_channel_account_Platform",
                table: "t_channel_account",
                column: "Platform",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_channel_binding_AccountId",
                table: "t_channel_binding",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_channel_binding_Platform",
                table: "t_channel_binding",
                column: "Platform",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_channel_cursor_AccountId",
                table: "t_channel_cursor",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_channel_inbox_AccountId_EventType_MessageId_MessageIndex",
                table: "t_channel_inbox",
                columns: new[] { "AccountId", "EventType", "MessageId", "MessageIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_channel_inbox_AccountId_Status_ReceivedAtUtc",
                table: "t_channel_inbox",
                columns: new[] { "AccountId", "Status", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_t_channel_outbox_ReplyRouteId_MessageSequence",
                table: "t_channel_outbox",
                columns: new[] { "ReplyRouteId", "MessageSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_channel_outbox_Status_NextAttemptAtUtc",
                table: "t_channel_outbox",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_t_channel_reply_route_AccountId_ExpiresAtUtc",
                table: "t_channel_reply_route",
                columns: new[] { "AccountId", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "t_channel_account");

            migrationBuilder.DropTable(
                name: "t_channel_binding");

            migrationBuilder.DropTable(
                name: "t_channel_cursor");

            migrationBuilder.DropTable(
                name: "t_channel_inbox");

            migrationBuilder.DropTable(
                name: "t_channel_outbox");

            migrationBuilder.DropTable(
                name: "t_channel_reply_route");
        }
    }
}
