using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quantum.Migrations.MySqlMigrations
{
    /// <inheritdoc />
    public partial class ChannelSafeCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "t_channel_allowed_command",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    CommandId = table.Column<string>(type: "nvarchar(64)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_channel_allowed_command", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_t_channel_allowed_command_AccountId_CommandId",
                table: "t_channel_allowed_command",
                columns: new[] { "AccountId", "CommandId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "t_channel_allowed_command");
        }
    }
}
