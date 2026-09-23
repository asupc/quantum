using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Data;

/// <summary>
/// 数据库初始化器：应用启动时由 Startup.Configure 调用一次，
/// 负责建库（无历史库时 EnsureCreated）、应用迁移（有历史库时）、种子数据。
/// DbContext 本身不再承载任何迁移/初始化逻辑，构造即轻量。
/// </summary>
public static class DbInitializer
{
    public static void Initialize(IServiceProvider serviceProvider)
    {
        var setting = SystemConfigHelper.GetSetting();
        if (setting.DBType.ToLower() == "sqlite")
        {
            Initialize(serviceProvider.GetRequiredService<DbContextOptions<QuantumSqliteDbContext>>(),
                () => new QuantumSqliteDbContext());
        }
        else
        {
            Initialize(serviceProvider.GetRequiredService<DbContextOptions<QuantumMySqlDbContext>>(),
                () => new QuantumMySqlDbContext());
        }
    }

    private static void Initialize<TContext>(DbContextOptions<TContext> options, Func<TContext> create)
        where TContext : DbContext, IQuantumDbContext
    {
        using var db = create();
        // 建库：EnsureCreated 仅在库不存在时建表；已有库时返回 false 不做任何事
        // 建库失败必须 fail-fast：带病启动只会把故障推迟到第一个请求才炸
        bool created;
        try
        {
            created = db.Database.EnsureCreated();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"检查/创建数据库失败，应用拒绝启动，请检查数据库连接配置：{e.Message}", e);
        }

        if (created)
        {
            Seed(db);
            // EnsureCreated 不写 __EFMigrationsHistory：立即把当前迁移链全部记为已应用，
            // 否则未来新增迁移时 GetPendingMigrations 返回全部、Migrate 对已存在表报错被兜底吞掉，
            // 新装库从此永远无法增量演进且无任何告警
            MarkAllMigrationsAsApplied(db);
            return;
        }

        // 已有库：应用挂起的迁移
        try
        {
            var pending = db.Database.GetPendingMigrations().ToList();
            if (pending.Count > 0)
            {
                var applied = db.Database.GetAppliedMigrations().ToList();
                if (applied.Count == 0 && HasFullCurrentSchema(db))
                {
                    // 库已存在、无任何迁移历史、且具备当前模型全量结构 = 早年 EnsureCreated 建的库：
                    // 与新装库同样回填迁移历史（后续迁移可增量应用），无需走迁移重放
                    MarkAllMigrationsAsApplied(db);
                    Console.WriteLine("检测到 EnsureCreated 存量库（无迁移历史），已回填全部迁移为已应用。");
                    return;
                }
                NormalizeLegacyConstraints(db);
                ApplyMigrationsResilient(db, pending);
            }
        }
        catch (Exception e)
        {
            // 仅「无迁移历史表（EnsureCreated 老库）」属预期跳过；带历史库的迁移执行失败是严重问题，
            // 必须以 ERROR 级留痕并给出指引——此前一律按“属预期”吞掉，导致库带病运行且无任何告警
            // （表现为接口层 Unknown column / 写入失败，极难溯源）。
            if (IsMissingHistoryTableError(e))
            {
                Console.WriteLine("应用迁移跳过（历史库无迁移记录，属预期）：" + e.Message);
            }
            else
            {
                Console.WriteLine($"[ERROR] 挂起迁移应用失败，库结构未更新（应用将以旧结构继续运行，相关功能可能报错）：{e.Message}");
                Console.WriteLine("[ERROR] 请人工核对数据库与 __EFMigrationsHistory（可先备份后重试启动，或用 dotnet ef database update 排查）。");
            }
        }
    }

    /// <summary>是否“库无迁移历史表”类错误（EnsureCreated 老库的预期形态）。</summary>
    private static bool IsMissingHistoryTableError(Exception e)
    {
        for (var cur = e; cur != null; cur = cur.InnerException)
        {
            var m = cur.Message;
            if (m.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
                || m.Contains("__EFMigrationsHistory", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 是否已具备当前模型的标志性结构（以最晚迁移建的表为准）：
    /// 用于区分「EnsureCreated 全量建出的库」与「迁移链中途断掉的库」（后者不能整链标已应用）。
    /// </summary>
    private static bool HasFullCurrentSchema<TContext>(TContext db) where TContext : DbContext
    {
        try
        {
            db.Database.OpenConnection();
            var conn = db.Database.GetDbConnection();
            using var cmd = conn.CreateCommand();
            // t_app_notify_setting 由迁移链最晚一条（NotifySetting）创建
            cmd.CommandText = db.Database.IsSqlite()
                ? "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='t_app_notify_setting'"
                : "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 't_app_notify_setting'";
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把当前迁移链全部记入 __EFMigrationsHistory（表不存在则先建，已存在则跳过该条）。
    /// 仅用于 EnsureCreated 建出的全量结构库——其结构即当前模型终点，与迁移链终点等价。
    /// </summary>
    public static void MarkAllMigrationsAsApplied<TContext>(TContext db) where TContext : DbContext
    {
        var migrations = db.Database.GetMigrations().ToList();
        if (migrations.Count == 0)
        {
            return;
        }
        db.Database.OpenConnection();
        var conn = db.Database.GetDbConnection();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = db.Database.IsSqlite()
                ? "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT NOT NULL CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY, ProductVersion TEXT NOT NULL)"
                : "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId VARCHAR(150) NOT NULL PRIMARY KEY, ProductVersion VARCHAR(32) NOT NULL)";
            cmd.ExecuteNonQuery();
        }
        foreach (var migration in migrations)
        {
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = $"SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = '{migration}'";
                if (Convert.ToInt64(probe.ExecuteScalar()) > 0)
                {
                    continue;
                }
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('{migration}', '10.0.12')";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 逐个应用迁移。存量库可能处于“迁移链中间态”：MySQL DDL 隐式提交，某次迁移中断后
    /// 其破坏性步骤（DropForeignKey/DropColumn 等）已生效但历史未记录，重跑同一迁移会因
    /// “约束/列不存在”再次失败。这里捕获该类错误：若失败点是其删除步骤已生效（错误信息含
    /// “does not exist”/“不存在”），把该迁移记入历史并继续后续迁移；其余错误正常抛出。
    /// 注意：MySQL 对 DropTable/DropIndex 目标缺失还会报 “Unknown table”/“check that column/key
    /// exists”——刻意**不**加入匹配：标记完成会跳过该迁移的其余步骤，若失败点靠前（如首步）
    /// 会造成“历史已记、结构未改”的撕裂态（2026-09-16 实测踩坑）；这类中间态应由
    /// NormalizeLegacyConstraints 预处理或人工补齐后完整重放。
    /// </summary>
    private static void ApplyMigrationsResilient<TContext>(TContext db, List<string> pending)
        where TContext : DbContext
    {
        var migrator = ((IInfrastructure<IServiceProvider>)db).Instance.GetRequiredService<IMigrator>();
        foreach (var migration in pending)
        {
            try
            {
                migrator.Migrate(migration);
            }
            catch (Exception e) when (IsAlreadyAppliedError(e))
            {
                Console.WriteLine($"迁移 {migration} 的删除步骤此前已生效（中间态库），标记完成并继续。");
                // 直接调 Migrate(下一迁移) 时 EF 会重跑全部 pending；改用 INSERT 历史记录绕过
                db.Database.OpenConnection();
                var conn = db.Database.GetDbConnection();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('{migration}', '10.0.12')";
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }

    private static bool IsAlreadyAppliedError(Exception e)
    {
        for (var cur = e; cur != null; cur = cur.InnerException)
        {
            var m = cur.Message;
            if (m.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                || m.Contains("不存在")
                || m.Contains("Duplicate key name", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 存量库预处理：把库结构对齐到迁移链的起点预期，避免首个迁移（NormalizationIndexes）中断。
    /// 处理两类历史偏差：
    /// 1. 外键命名偏差——早期库外键名是 MySQL 自动命名（t_task_sub_ibfk_1），
    ///    迁移按 EF 约定名 FK_t_task_sub_t_task_TaskModelId 删除时报“约束不存在”；
    /// 2. 部分执行残留——此前迁移失败回滚后可能留下半套结构（列已删/外键已无）。
    /// 策略：列在则保证外键/索引以 EF 约定名存在（先删现存外键再补建），列不在则什么都不做。
    /// </summary>
    private static void NormalizeLegacyConstraints<TContext>(TContext db) where TContext : DbContext
    {
        // 仅 MySQL 有此问题：SQLite 的 EnsureCreated 外键由 EF 建表时命名，与迁移一致
        if (db.Database.IsSqlite())
        {
            return;
        }
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();

            bool ColumnExists(string table, string column)
            {
                using var cmd = conn.CreateCommand();
                // 注意：information_schema.COLUMNS 没有 CONSTRAINT_SCHEMA 列（那是 KEY_COLUMN_USAGE 的），
                // 引用会直接报 Unknown column 使整个预处理被跳过（2026-09-16 MySQL 8.4 实测）
                cmd.CommandText = $@"SELECT COUNT(*) FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'";
                return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
            }

            void Exec(string sql)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            const string efFk = "FK_t_task_sub_t_task_TaskModelId";
            if (ColumnExists("t_task_sub", "TaskModelId"))
            {
                // 列在：把该列上的任意外键统一成 EF 约定名（无外键则补建），迁移 DropForeignKey 才能执行
                string actual = null;
                using (var probe = conn.CreateCommand())
                {
                    probe.CommandText = @"SELECT CONSTRAINT_NAME FROM information_schema.KEY_COLUMN_USAGE
WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 't_task_sub' AND COLUMN_NAME = 'TaskModelId'
AND REFERENCED_TABLE_NAME IS NOT NULL LIMIT 1";
                    actual = probe.ExecuteScalar() as string;
                }
                if (!string.IsNullOrEmpty(actual) && actual != efFk)
                {
                    Exec($"ALTER TABLE `t_task_sub` DROP FOREIGN KEY `{actual}`");
                    Console.WriteLine($"存量库外键名对齐：{actual} → {efFk}");
                }
                if (string.IsNullOrEmpty(actual) || actual != efFk)
                {
                    Exec($"ALTER TABLE `t_task_sub` ADD CONSTRAINT `{efFk}` FOREIGN KEY (`TaskModelId`) REFERENCES `t_task` (`Id`)");
                    Console.WriteLine($"存量库外键补建：{efFk}");
                }
                // 迁移还会 DropIndex IX_t_task_sub_TaskModelId：保证该索引以约定名存在
                using (var probe = conn.CreateCommand())
                {
                    probe.CommandText = @"SELECT COUNT(*) FROM information_schema.STATISTICS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 't_task_sub' AND INDEX_NAME = 'IX_t_task_sub_TaskModelId'";
                    if (Convert.ToInt64(probe.ExecuteScalar()) == 0)
                    {
                        Exec("ALTER TABLE `t_task_sub` ADD INDEX `IX_t_task_sub_TaskModelId` (`TaskModelId`)");
                        Console.WriteLine("存量库索引补建：IX_t_task_sub_TaskModelId");
                    }
                }
            }
            else
            {
                // 列不在（此前迁移已部分执行）：把该迁移标记为已应用等效——
                // 无法回填中间态，直接按需重建缺失结构代价过高；改为提示人工介入。
                Console.WriteLine("检测到 t_task_sub.TaskModelId 已不存在（迁移链曾部分执行）。");
                Console.WriteLine("将尝试直接应用后续迁移；若失败需人工核对 __EFMigrationsHistory。");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("存量库预处理跳过：" + e.Message);
        }
    }

    /// <summary>
    /// 新库种子数据。
    /// </summary>
    private static void Seed(IQuantumDbContext db)
    {
        db.Commands.Add(new CommandModel
        {
            Enable = true,
            Key = "你好",
            Message = "你好，欢迎使用量子助手。",
        });
        db.SaveChanges();
    }
}
