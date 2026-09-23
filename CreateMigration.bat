@echo off
rem 使用 UTF-8 代码页，保证中文注释与提示正确显示
chcp 65001 >nul
rem ===================================================================================
rem 迁移生成脚本（多项目分层结构版）：
rem DbContext 与迁移类位于 Quantum.Data 项目，宿主为 Quantum.Web（AssemblyName=Quantum）。
rem 新增迁移须分别对两个 DbContext 各执行一次（替换 <迁移名> 后运行本脚本）：
rem
rem 用法：CreateMigration.bat <迁移名>
rem 产物：Quantum.API/Quantum.Data/Migrations/{SqliteMigrations,MySqlMigrations}/<时间戳>_<迁移名>.cs
rem 应用：启动时 DbInitializer 按 appsettings.json 的 DBType 自动应用对应侧迁移。
rem ===================================================================================
cd /d "%~dp0Quantum.API"
set migrationName=%1
if "%migrationName%" EQU "" set /p migrationName=请输入迁移名：

echo.
echo === Add Migration for QuantumSqliteDbContext ===
dotnet-ef migrations add %migrationName% --project Quantum.Data --startup-project Quantum.Web --context QuantumSqliteDbContext --output-dir Migrations/SqliteMigrations --namespace Quantum.Migrations.SqliteMigrations
if errorlevel 1 goto :failed

echo.
echo === Add Migration for QuantumMySqlDbContext ===
dotnet-ef migrations add %migrationName% --project Quantum.Data --startup-project Quantum.Web --context QuantumMySqlDbContext --output-dir Migrations/MySqlMigrations --namespace Quantum.Migrations.MySqlMigrations
if errorlevel 1 goto :failed

echo.
echo 双库迁移迁移生成完毕，请检查两份 Up/Down 内容后提交。
goto :eof

:failed
echo.
echo 迁移生成失败，请检查 dotnet-ef 工具（dotnet tool install --global dotnet-ef）与编译错误。
exit /b 1
