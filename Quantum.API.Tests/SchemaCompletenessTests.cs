using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Quantum.Data;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 「库是否已具备当前模型」判定的回归（沙箱恢复生产备份时实测到的缺陷）：
/// 旧实现只查一张"最近建的表"当代理，任何后续新增迁移都会被整链误标为已应用，
/// 新表永不创建、功能在运行期静默失败。本类钉住"缺表/缺列必须判不完整"。
/// </summary>
public class SchemaCompletenessTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;

    public SchemaCompletenessTests()
    {
        (_connection, _db) = AppTestDb.Create();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Execute(string sql) => _db.Database.ExecuteSqlRaw(sql);

    [Fact]
    public void EnsureCreatedDb_WithFullModel_IsConsideredComplete()
    {
        Assert.True(DbInitializer.HasFullCurrentSchema(_db));
    }

    [Fact]
    public void MissingNewTable_IsNotComplete()
    {
        Execute("DROP TABLE t_task_run");

        Assert.False(DbInitializer.HasFullCurrentSchema(_db));
    }

    [Fact]
    public void MissingNewColumn_IsNotComplete()
    {
        // SQLite 不支持 DROP COLUMN 的老版本路径，用重建表的方式模拟"表在但列缺"
        Execute("ALTER TABLE t_chat_session RENAME TO t_chat_session_old");
        Execute("CREATE TABLE t_chat_session (sessionKey TEXT NOT NULL PRIMARY KEY, createTime TEXT NOT NULL, lastSeq INTEGER NOT NULL, lastReadSeq INTEGER NOT NULL)");

        Assert.False(DbInitializer.HasFullCurrentSchema(_db));
    }

    [Fact]
    public void InitOnlyLegacyDb_IsNotComplete_MustNotBeBackfilledAsApplied()
    {
        // 复现生产形态：无迁移历史（EnsureCreated 建出的库本来就没有这张表）+ 老结构齐全 + 新表全缺
        foreach (var table in new[]
                 {
                     "t_task_run", "t_task_failure_policy", "t_task_alert_state", "t_task_alert_event",
                     "t_external_push_credential", "t_external_push_request",
                     "t_channel_account", "t_channel_binding", "t_channel_cursor",
                     "t_channel_inbox", "t_channel_outbox", "t_channel_reply_route", "t_channel_allowed_command"
                 })
        {
            Execute($"DROP TABLE {table}");
        }

        Assert.False(DbInitializer.HasFullCurrentSchema(_db));
        // 整链都算挂起；新增通道迁移后不能把未创建的新表回填为已应用。
        // 计数按「本上下文迁移类的总数」推导，不写死数字——否则每加一条迁移都要来改一次断言。
        var total = typeof(QuantumSqliteDbContext).Assembly.GetTypes()
            .Count(t => t.Namespace == "Quantum.Migrations.SqliteMigrations"
                        && typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract);
        Assert.True(total > 0, "未找到 SQLite 侧迁移类，断言失去意义");
        Assert.Equal(total, _db.Database.GetPendingMigrations().Count());
    }
}
