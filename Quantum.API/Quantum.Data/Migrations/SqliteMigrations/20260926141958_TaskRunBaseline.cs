using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quantum.Migrations.SqliteMigrations
{
    /// <inheritdoc />
    public partial class TaskRunBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "t_task_alert_event",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    TaskId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    RootRunId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    AlertType = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    SafeSummary = table.Column<string>(type: "TEXT", nullable: true),
                    DeliveryStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveryAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveryError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_task_alert_event", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_task_alert_state",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    TaskId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    Open = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastFinalRunId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_task_alert_state", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_task_failure_policy",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    TaskId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BackoffSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    AlertAfterConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    SendRecovery = table.Column<bool>(type: "INTEGER", nullable: false),
                    CooldownMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_task_failure_policy", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "t_task_run",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    RootRunId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    TaskNameSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    ScriptFileSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    ScriptHashSnapshot = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    TriggerSource = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggerRef = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureCode = table.Column<int>(type: "INTEGER", nullable: false),
                    SafeSummary = table.Column<string>(type: "TEXT", nullable: true),
                    LogDirectoryName = table.Column<string>(type: "TEXT", nullable: true),
                    LogFileName = table.Column<string>(type: "TEXT", nullable: true),
                    LogId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ManagerSnapshot = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRetry = table.Column<bool>(type: "INTEGER", nullable: false),
                    CancelReason = table.Column<string>(type: "TEXT", nullable: true),
                    RetryDeferrals = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_task_run", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_t_task_alert_event_DeliveryStatus_CreatedAtUtc",
                table: "t_task_alert_event",
                columns: new[] { "DeliveryStatus", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_t_task_alert_event_TaskId_AlertType_RootRunId",
                table: "t_task_alert_event",
                columns: new[] { "TaskId", "AlertType", "RootRunId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_task_alert_state_TaskId",
                table: "t_task_alert_state",
                column: "TaskId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_task_failure_policy_TaskId",
                table: "t_task_failure_policy",
                column: "TaskId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_task_run_LogId",
                table: "t_task_run",
                column: "LogId");

            migrationBuilder.CreateIndex(
                name: "IX_t_task_run_RootRunId_Attempt",
                table: "t_task_run",
                columns: new[] { "RootRunId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_task_run_Status_NextAttemptAtUtc",
                table: "t_task_run",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_t_task_run_TaskId_CreatedAtUtc",
                table: "t_task_run",
                columns: new[] { "TaskId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "t_task_alert_event");

            migrationBuilder.DropTable(
                name: "t_task_alert_state");

            migrationBuilder.DropTable(
                name: "t_task_failure_policy");

            migrationBuilder.DropTable(
                name: "t_task_run");
        }
    }
}
