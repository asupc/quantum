using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Quantum.Data;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 迁移链可执行性冒烟（一期 G2/G5 门禁）：既有测试都走 <c>EnsureCreated</c>（按模型直接建表），
/// 只能证明「模型自洽」，证明不了迁移 SQL 本身能跑。本类用 <c>Migrate</c> 从空库依次应用
/// Init → TaskRunBaseline → ExternalPushBaseline 全链，断言新表与新列真的被建出来。
/// 本类自动验证 SQLite；MySQL 8.4 的本次删列迁移另在隔离容器完成 Up/Down 演练。
/// </summary>
public class MigrationChainTests
{
    private static (SqliteConnection connection, QuantumSqliteDbContext db) OpenMigrated()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<QuantumSqliteDbContext>().UseSqlite(connection).Options;
        var db = new QuantumSqliteDbContext(options);
        db.Database.Migrate();
        return (connection, db);
    }

    private static List<string> Tables(QuantumSqliteDbContext db)
    {
        var rows = new List<string>();
        var conn = db.Database.GetDbConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }

    private static List<string> ColumnsOf(QuantumSqliteDbContext db, string table)
    {
        var rows = new List<string>();
        var conn = db.Database.GetDbConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(reader.GetString(1));
        }
        return rows;
    }

    [Fact]
    public void Migrate_FromEmptyDb_CreatesAllG1ToGPushTables()
    {
        var (connection, db) = OpenMigrated();
        using (connection)
        using (db)
        {
            var tables = Tables(db);
            Assert.Contains("t_task_run", tables);
            Assert.Contains("t_task_failure_policy", tables);
            Assert.Contains("t_task_alert_state", tables);
            Assert.Contains("t_task_alert_event", tables);
            Assert.Contains("t_external_push_credential", tables);
            Assert.Contains("t_external_push_request", tables);
            Assert.DoesNotContain("Manager", ColumnsOf(db, "t_task"));
            Assert.DoesNotContain("ManagerSnapshot", ColumnsOf(db, "t_task_run"));
        }
    }

    [Fact]
    public void LegacyEnsureCreatedColumns_AreDroppedWithoutLosingRows()
    {
        var (connection, db) = OpenMigrated();
        using (connection)
        using (db)
        {
            db.Tasks.Add(new Entities.Model.TaskModel { Id = "T1", Name = "旧任务" });
            db.TaskRuns.Add(new Entities.Model.TaskRunModel
            {
                Id = "R1", RootRunId = "R1", TaskId = "T1", Attempt = 1,
                TriggerSource = Entities.Model.TaskTriggerSource.Manual,
                Status = Entities.Model.TaskRunStatus.Succeeded
            });
            db.SaveChanges();
            db.Database.ExecuteSqlRaw("ALTER TABLE t_task ADD COLUMN Manager INTEGER NOT NULL DEFAULT 0");
            db.Database.ExecuteSqlRaw("ALTER TABLE t_task_run ADD COLUMN ManagerSnapshot INTEGER NOT NULL DEFAULT 0");
            db.Database.ExecuteSqlRaw("UPDATE t_task SET Manager = 1 WHERE Id = 'T1'");
            db.Database.ExecuteSqlRaw("UPDATE t_task_run SET ManagerSnapshot = 1 WHERE Id = 'R1'");

            DbInitializer.RemoveLegacyRoleColumns(db);

            Assert.DoesNotContain("Manager", ColumnsOf(db, "t_task"));
            Assert.DoesNotContain("ManagerSnapshot", ColumnsOf(db, "t_task_run"));
            Assert.Equal("旧任务", db.Tasks.AsNoTracking().Single().Name);
            Assert.Equal("R1", db.TaskRuns.AsNoTracking().Single().Id);
        }
    }

    [Fact]
    public void RemoveTaskRolesMigration_PreservesRowsWithBothOldValues()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<QuantumSqliteDbContext>().UseSqlite(connection).Options;
        using var db = new QuantumSqliteDbContext(options);
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("ALTER TABLE t_task ADD COLUMN Manager INTEGER NOT NULL DEFAULT 0");
        db.Database.ExecuteSqlRaw("ALTER TABLE t_task_run ADD COLUMN ManagerSnapshot INTEGER NOT NULL DEFAULT 0");
        db.Tasks.AddRange(
            new Entities.Model.TaskModel { Id = "T0", Name = "旧任务零" },
            new Entities.Model.TaskModel { Id = "T1", Name = "旧任务一" });
        db.TaskRuns.AddRange(
            new Entities.Model.TaskRunModel
            {
                Id = "R0", RootRunId = "R0", TaskId = "T0", Attempt = 1,
                TriggerSource = Entities.Model.TaskTriggerSource.Manual,
                Status = Entities.Model.TaskRunStatus.Succeeded
            },
            new Entities.Model.TaskRunModel
            {
                Id = "R1", RootRunId = "R1", TaskId = "T1", Attempt = 1,
                TriggerSource = Entities.Model.TaskTriggerSource.Manual,
                Status = Entities.Model.TaskRunStatus.Succeeded
            });
        db.SaveChanges();
        db.Database.ExecuteSqlRaw("UPDATE t_task SET Manager = 1 WHERE Id = 'T1'");
        db.Database.ExecuteSqlRaw("UPDATE t_task_run SET ManagerSnapshot = 1 WHERE Id = 'R1'");

        DbInitializer.MarkAllMigrationsAsApplied(db);
        var lastMigration = db.Database.GetMigrations().Last();
        db.Database.ExecuteSqlRaw("DELETE FROM __EFMigrationsHistory WHERE MigrationId = {0}", lastMigration);
        db.Database.Migrate();

        Assert.DoesNotContain("Manager", ColumnsOf(db, "t_task"));
        Assert.DoesNotContain("ManagerSnapshot", ColumnsOf(db, "t_task_run"));
        Assert.Equal(2, db.Tasks.AsNoTracking().Count());
        Assert.Equal(2, db.TaskRuns.AsNoTracking().Count());
        Assert.Empty(db.Database.GetPendingMigrations());
    }

    [Fact]
    public void RemoveTaskRolesMigration_DownRestoresDefaultColumns()
    {
        var (connection, db) = OpenMigrated();
        using (connection)
        using (db)
        {
            var previous = db.Database.GetMigrations().Reverse().Skip(1).First();
            db.GetService<IMigrator>().Migrate(previous);

            Assert.Contains("Manager", ColumnsOf(db, "t_task"));
            Assert.Contains("ManagerSnapshot", ColumnsOf(db, "t_task_run"));

            db.Database.Migrate();
            Assert.DoesNotContain("Manager", ColumnsOf(db, "t_task"));
            Assert.DoesNotContain("ManagerSnapshot", ColumnsOf(db, "t_task_run"));
        }
    }

    [Fact]
    public void Migrate_AddsSessionTitleColumnsAndUniqueIndexes()
    {
        var (connection, db) = OpenMigrated();
        using (connection)
        using (db)
        {
            Assert.Contains("DisplayTitle", ColumnsOf(db, "t_chat_session"));
            Assert.Contains("SessionTitle", ColumnsOf(db, "t_chat_message"));

            // 直接查 sqlite_master 的建索引语句：PRAGMA index_list 的列序/类型随 SQLite 版本漂移，
            // 而 `CREATE UNIQUE INDEX` 文本就是「这个唯一约束真的存在」的硬证据
            var uniqueIndexes = new List<string>();
            var conn = db.Database.GetDbConnection();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND sql LIKE 'CREATE UNIQUE INDEX%'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    uniqueIndexes.Add(reader.GetString(0));
                }
            }

            Assert.Contains("IX_t_task_run_RootRunId_Attempt", uniqueIndexes);
            Assert.Contains("IX_t_task_failure_policy_TaskId", uniqueIndexes);
            Assert.Contains("IX_t_task_alert_event_TaskId_AlertType_RootRunId", uniqueIndexes);
            Assert.Contains("IX_t_external_push_request_CredentialId_IdempotencyKey", uniqueIndexes);
        }
    }

    [Fact]
    public async Task MigratedDb_SupportsRunAndPolicyWrites()
    {
        var (connection, db) = OpenMigrated();
        using (connection)
        using (db)
        {
            db.TaskRuns.Add(new Entities.Model.TaskRunModel
            {
                Id = "R1", RootRunId = "R1", TaskId = "T1", Attempt = 1,
                TriggerSource = Entities.Model.TaskTriggerSource.Manual,
                Status = Entities.Model.TaskRunStatus.Succeeded
            });
            db.TaskFailurePolicies.Add(new Entities.Model.TaskFailurePolicyModel
            {
                TaskId = "T1", RetryCount = 2, Enabled = true
            });
            await db.SaveChangesAsync();

            Assert.NotNull(await db.TaskRuns.AsNoTracking().SingleAsync(n => n.Id == "R1"));
            Assert.Equal(2, (await db.TaskFailurePolicies.AsNoTracking().SingleAsync(n => n.TaskId == "T1")).RetryCount);
            Assert.Empty(db.Database.GetPendingMigrations());
        }
    }

    [Fact]
    public void DuplicateRootAttempt_IsRejectedByUniqueIndex()
    {
        var (connection, db) = OpenMigrated();
        using (connection)
        using (db)
        {
            db.TaskRuns.AddRange(
                new Entities.Model.TaskRunModel
                {
                    Id = "A", RootRunId = "A", TaskId = "T1", Attempt = 1,
                    TriggerSource = Entities.Model.TaskTriggerSource.Manual,
                    Status = Entities.Model.TaskRunStatus.Pending
                },
                new Entities.Model.TaskRunModel
                {
                    Id = "B", RootRunId = "A", TaskId = "T1", Attempt = 1,
                    TriggerSource = Entities.Model.TaskTriggerSource.Manual,
                    Status = Entities.Model.TaskRunStatus.Pending
                });

            Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
        }
    }
}
