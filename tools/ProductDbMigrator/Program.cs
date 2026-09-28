// 目标库结构迁移工具（一次性运维工具，不随产品发布）：
// 从 Quantum.Web/appsettings.json 程序内读取连接串，仅内存中把库名替换为目标库，
// 再按 DbInitializer.Initialize 的 MySQL 分支语义执行建库/回填/增量迁移，保证与
// 生产启动行为一致（含 EnsureCreated 存量库回填、中断中间态容错、遗留外键名对齐）。
// 用法：
//   dotnet run --project tools/ProductDbMigrator            → probe 只读探查
//   dotnet run --project tools/ProductDbMigrator -- migrate → 执行迁移并验证
// 可选第二参数：目标库名（默认 quantum_product）
using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Quantum.Data;
using Quantum.Entities.Model;

internal static class Program
{
    private static readonly string[] ChannelTables =
    [
        "t_channel_account", "t_channel_binding", "t_channel_cursor", "t_channel_inbox",
        "t_channel_outbox", "t_channel_reply_route", "t_channel_allowed_command",
    ];

    private static int Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "probe";
        var targetDb = args.Skip(1).FirstOrDefault() ?? "quantum_product";

        var root = FindRepoRoot();
        var settingsPath = Path.Combine(root, "Quantum.API", "Quantum.Web", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        if (!doc.RootElement.TryGetProperty("Quantum", out var section)
            || !section.TryGetProperty("DBAddress", out var addrEl))
        {
            Console.WriteLine("appsettings.json 缺少 Quantum:DBAddress");
            return 1;
        }

        var dbType = section.TryGetProperty("DBType", out var typeEl) ? typeEl.GetString() : null;
        if (dbType is null || !dbType.StartsWith("mysql", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"DBType={dbType} 非 MySQL，本工具只处理 MySQL 目标库");
            return 1;
        }

        var address = addrEl.GetString();
        if (string.IsNullOrWhiteSpace(address))
        {
            Console.WriteLine("Quantum:DBAddress 为空");
            return 1;
        }

        // 仅替换库名段，其余（服务器/账号/口令/字符集）保持生产配置原样
        var conn = Regex.Replace(address, @"(?i)(database\s*=\s*)[^;]*", $"$1{targetDb}");
        conn = QuantumMySqlDbContext.NormalizeCharsetToUtf8mb4(conn);
        Console.WriteLine($"目标库：{targetDb}");
        Console.WriteLine($"服务器：{Mask(conn)}");

        var options = new DbContextOptionsBuilder<QuantumMySqlDbContext>().UseMySQL(conn).Options;

        // 服务器级探查：库是否存在（连接串去掉 database 段连 information_schema）
        var serverConn = Regex.Replace(conn, @"(?i)\bdatabase\s*=\s*[^;]*;?", "");
        bool dbExists;
        using (var sc = new MySql.Data.MySqlClient.MySqlConnection(serverConn))
        {
            sc.Open();
            using var cmd = sc.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @name";
            cmd.Parameters.AddWithValue("@name", targetDb);
            dbExists = Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        Console.WriteLine($"库存在：{dbExists}");

        if (!dbExists)
        {
            if (mode != "migrate")
            {
                Console.WriteLine("probe：库尚不存在，执行 migrate 模式时将全量建库。");
                return 0;
            }

            Console.WriteLine("库不存在，走 EnsureCreated 全量建库（对齐生产新装路径）…");
            using var db = new QuantumMySqlDbContext(options);
            if (!db.Database.EnsureCreated())
            {
                Console.WriteLine("EnsureCreated 返回 false（并发建库？），请重跑。");
                return 1;
            }

            Seed(db);
            DbInitializer.MarkAllMigrationsAsApplied(db);
            Console.WriteLine("全量建库完成，迁移链已整体回填 __EFMigrationsHistory。");
            return Verify(db, targetDb) ? 0 : 1;
        }

        // 库已存在：读取迁移历史与结构状态
        using var ctx = new QuantumMySqlDbContext(options);
        var applied = ctx.Database.GetAppliedMigrations().ToList();
        var pending = ctx.Database.GetPendingMigrations().ToList();
        Console.WriteLine($"已应用迁移：{(applied.Count == 0 ? "（无历史记录）" : string.Join(", ", applied))}");
        Console.WriteLine($"挂起迁移：{(pending.Count == 0 ? "（无）" : string.Join(", ", pending))}");

        if (mode != "migrate")
        {
            var (tables, counts) = LoadSchemaColumns(ctx);
            Console.WriteLine($"现有表数：{tables.Count}");
            foreach (var t in ChannelTables.Where(tables.ContainsKey))
            {
                Console.WriteLine($"  通道表已存在：{t}（{counts[t]} 列）");
            }

            Console.WriteLine("probe 结束（未做任何变更）。");
            return 0;
        }

        if (pending.Count == 0)
        {
            Console.WriteLine("无挂起迁移，仅需结构完整性校验。");
            return Verify(ctx, targetDb) ? 0 : 1;
        }

        // 迁移执行：严格对齐 DbInitializer.Initialize 的既有库分支
        if (applied.Count == 0 && HasFullCurrentSchema(ctx))
        {
            DbInitializer.MarkAllMigrationsAsApplied(ctx);
            Console.WriteLine("EnsureCreated 存量库（无历史、结构完整）：已回填迁移历史，无需重放。");
            return Verify(ctx, targetDb) ? 0 : 1;
        }

        NormalizeLegacyConstraints(ctx);
        ResilientApply(ctx, pending);
        return Verify(ctx, targetDb) ? 0 : 1;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Quantum.API", "Quantum.Web", "appsettings.json");
            if (File.Exists(candidate))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("未定位到 Quantum.API/Quantum.Web/appsettings.json");
    }

    private static string Mask(string conn) => Regex.Replace(conn, @"(?i)(pwd\s*=\s*)[^;]*", "$1***");

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

    /// <summary>读库里实际存在的表→列集合（MySQL 走 information_schema）。</summary>
    private static (Dictionary<string, HashSet<string>> Tables, Dictionary<string, int> Counts) LoadSchemaColumns(DbContext db)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        db.Database.OpenConnection();
        try
        {
            using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT TABLE_NAME, COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var table = reader.GetString(0);
                if (!map.TryGetValue(table, out var columns))
                {
                    columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[table] = columns;
                }

                columns.Add(reader.GetString(1));
            }
        }
        finally
        {
            db.Database.CloseConnection();
        }

        return (map, map.ToDictionary(p => p.Key, p => p.Value.Count, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>库是否已具备当前模型的全部表与列（DbInitializer.HasFullCurrentSchema 的等价复制）。</summary>
    private static bool HasFullCurrentSchema(DbContext db)
    {
        try
        {
            var expected = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entityType in db.Model.GetEntityTypes())
            {
                var table = entityType.GetTableName();
                if (string.IsNullOrEmpty(table))
                {
                    continue;
                }

                if (!expected.TryGetValue(table, out var columns))
                {
                    columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    expected[table] = columns;
                }

                var storeTable = StoreObjectIdentifier.Table(table, entityType.GetSchema());
                foreach (var property in entityType.GetProperties())
                {
                    var column = property.GetColumnName(storeTable);
                    if (!string.IsNullOrEmpty(column))
                    {
                        columns.Add(column);
                    }
                }
            }

            var (actual, _) = LoadSchemaColumns(db);
            foreach (var pair in expected)
            {
                if (!actual.TryGetValue(pair.Key, out var have))
                {
                    Console.WriteLine($"库结构不完整：缺表 {pair.Key}");
                    return false;
                }

                foreach (var column in pair.Value)
                {
                    if (!have.Contains(column))
                    {
                        Console.WriteLine($"库结构不完整：{pair.Key} 缺列 {column}");
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine("结构完整性判定异常：" + e.Message);
            return false;
        }
    }

    /// <summary>存量库外键/索引名对齐（DbInitializer.NormalizeLegacyConstraints 的等价复制）。</summary>
    private static void NormalizeLegacyConstraints(DbContext db)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();

            bool ColumnExists(string table, string column)
            {
                using var cmd = conn.CreateCommand();
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
                string actual;
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
        }
        catch (Exception e)
        {
            Console.WriteLine("存量库预处理跳过：" + e.Message);
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    /// <summary>逐个应用迁移并对中间态容错（DbInitializer.ApplyMigrationsResilient 的等价复制）。</summary>
    private static void ResilientApply(DbContext db, List<string> pending)
    {
        var migrator = ((IInfrastructure<IServiceProvider>)db).Instance.GetRequiredService<IMigrator>();
        foreach (var migration in pending)
        {
            try
            {
                Console.WriteLine($"应用迁移：{migration} …");
                migrator.Migrate(migration);
                Console.WriteLine($"迁移完成：{migration}");
            }
            catch (Exception e) when (IsAlreadyAppliedError(e))
            {
                Console.WriteLine($"迁移 {migration} 的删除步骤此前已生效（中间态库），标记完成并继续。");
                db.Database.OpenConnection();
                try
                {
                    using var cmd = db.Database.GetDbConnection().CreateCommand();
                    cmd.CommandText = $"INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('{migration}', '10.0.12')";
                    cmd.ExecuteNonQuery();
                }
                finally
                {
                    db.Database.CloseConnection();
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

    private static bool Verify(DbContext db, string targetDb)
    {
        var ok = true;

        var history = new List<string>();
        db.Database.OpenConnection();
        try
        {
            using (var cmd = db.Database.GetDbConnection().CreateCommand())
            {
                cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    history.Add(reader.GetString(0));
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[FAIL] __EFMigrationsHistory 不可读：{e.Message}");
            ok = false;
        }
        finally
        {
            db.Database.CloseConnection();
        }

        Console.WriteLine($"迁移历史 {history.Count} 条：{string.Join(", ", history)}");

        var (tables, counts) = LoadSchemaColumns(db);
        foreach (var t in ChannelTables)
        {
            if (tables.TryGetValue(t, out var cols))
            {
                Console.WriteLine($"[OK] 通道表 {t}（{counts[t]} 列）");
            }
            else
            {
                Console.WriteLine($"[FAIL] 缺通道表 {t}");
                ok = false;
            }
        }

        if (HasFullCurrentSchema(db))
        {
            Console.WriteLine("[OK] 全模型结构完整性校验通过（所有实体表与列齐备）。");
        }
        else
        {
            ok = false;
        }

        Console.WriteLine($"校验结论：{(ok ? $"{targetDb} 结构迁移完成" : "存在缺失项，请核对上方 [FAIL] 输出")}");
        return ok;
    }
}
