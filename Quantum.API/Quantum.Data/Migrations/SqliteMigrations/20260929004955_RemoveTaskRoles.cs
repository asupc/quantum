using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quantum.Migrations.SqliteMigrations
{
    /// <inheritdoc />
    public partial class RemoveTaskRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManagerSnapshot",
                table: "t_task_run");

            migrationBuilder.DropColumn(
                name: "Manager",
                table: "t_task");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ManagerSnapshot",
                table: "t_task_run",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Manager",
                table: "t_task",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
