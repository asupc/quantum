using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Quantum.Data;

namespace Quantum.API.Tests;

/// <summary>双库通道迁移：表与唯一约束 Up/Down 逐项一致，SQLite 真实升级/回滚可重做。</summary>
public sealed class ChannelMigrationTests
{
    [Fact]
    public void SqliteAndMySql_CreateAndDropTheSameChannelTablesAndIndexes()
    {
        var sqlite = Operations(new Quantum.Migrations.SqliteMigrations.ChannelCore(), "Microsoft.EntityFrameworkCore.Sqlite");
        var mysql = Operations(new Quantum.Migrations.MySqlMigrations.ChannelCore(), "MySQL");
        var expectedTables = new[] { "t_channel_account", "t_channel_binding", "t_channel_cursor",
            "t_channel_inbox", "t_channel_outbox", "t_channel_reply_route" };
        Assert.Equal(expectedTables.Order(), sqlite.up.OfType<CreateTableOperation>().Select(x => x.Name).Order());
        Assert.Equal(expectedTables.Order(), mysql.up.OfType<CreateTableOperation>().Select(x => x.Name).Order());
        Assert.Equal(expectedTables.Order(), sqlite.down.OfType<DropTableOperation>().Select(x => x.Name).Order());
        Assert.Equal(expectedTables.Order(), mysql.down.OfType<DropTableOperation>().Select(x => x.Name).Order());
        static string Key(CreateIndexOperation x) => x.Table + ":" + string.Join(",", x.Columns) + ":" + x.IsUnique;
        Assert.Equal(sqlite.up.OfType<CreateIndexOperation>().Select(Key).Order(),
            mysql.up.OfType<CreateIndexOperation>().Select(Key).Order());
        Assert.Contains(sqlite.up.OfType<CreateIndexOperation>(), x => x.Table == "t_channel_account" &&
            x.Columns.SequenceEqual(["Platform"]) && x.IsUnique);
        Assert.Contains(sqlite.up.OfType<CreateIndexOperation>(), x => x.Table == "t_channel_inbox" &&
            x.Columns.SequenceEqual(["AccountId", "EventType", "MessageId", "MessageIndex"]) && x.IsUnique);
    }

    [Fact]
    public void SqliteMigration_CreatesChannelTables_AndCanRollBack()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new QuantumSqliteDbContext(new DbContextOptionsBuilder<QuantumSqliteDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
        Assert.Contains("t_channel_account", db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type='table' AND name='t_channel_account'").ToList());
        var applied = db.Database.GetAppliedMigrations().ToList();
        // 按名字定位而非「倒数第几」：链条后面再加迁移（如删列迁移）不该把本用例撞失败
        var core = applied.FindIndex(x => x.Contains("ChannelCore"));
        Assert.True(core >= 0, "迁移链中缺少 ChannelCore");
        Assert.Contains("ChannelSafeCommands", applied[core + 1]);
        Assert.Contains("ChannelRequiredKeys", applied[core + 2]);
        var previous = applied[core - 1];
        db.Database.Migrate(previous);
        var schema = db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table' AND name='t_channel_account'").ToList();
        Assert.Empty(schema);
        db.Database.Migrate();
        Assert.Contains("t_channel_account", db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type='table' AND name='t_channel_account'").ToList());
        Assert.Contains("t_channel_allowed_command", db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type='table' AND name='t_channel_allowed_command'").ToList());
    }

    [Fact]
    public void SafeCommandMigration_IsSymmetricAcrossBothDatabases()
    {
        var sqlite = Operations(new Quantum.Migrations.SqliteMigrations.ChannelSafeCommands(), "Microsoft.EntityFrameworkCore.Sqlite");
        var mysql = Operations(new Quantum.Migrations.MySqlMigrations.ChannelSafeCommands(), "MySQL");
        Assert.Equal(["t_channel_allowed_command"], sqlite.up.OfType<CreateTableOperation>().Select(x => x.Name));
        Assert.Equal(["t_channel_allowed_command"], mysql.up.OfType<CreateTableOperation>().Select(x => x.Name));
        Assert.Equal(["t_channel_allowed_command"], sqlite.down.OfType<DropTableOperation>().Select(x => x.Name));
        Assert.Equal(["t_channel_allowed_command"], mysql.down.OfType<DropTableOperation>().Select(x => x.Name));
        Assert.Contains(sqlite.up.OfType<CreateIndexOperation>(), x => x.IsUnique &&
            x.Columns.SequenceEqual(["AccountId", "CommandId"]));
        Assert.Equal(sqlite.up.OfType<CreateIndexOperation>().Select(x => string.Join(",", x.Columns)),
            mysql.up.OfType<CreateIndexOperation>().Select(x => string.Join(",", x.Columns)));
    }

    [Fact]
    public void RequiredKeyMigration_MatchesBothProviders()
    {
        var sqlite = Operations(new Quantum.Migrations.SqliteMigrations.ChannelRequiredKeys(), "Microsoft.EntityFrameworkCore.Sqlite");
        var mysql = Operations(new Quantum.Migrations.MySqlMigrations.ChannelRequiredKeys(), "MySQL");
        static string Key(AlterColumnOperation x) => x.Table + ":" + x.Name + ":" + x.IsNullable;
        Assert.Equal(sqlite.up.OfType<AlterColumnOperation>().Select(Key).Order(),
            mysql.up.OfType<AlterColumnOperation>().Select(Key).Order());
        Assert.All(sqlite.up.OfType<AlterColumnOperation>(), x => Assert.False(x.IsNullable));
        Assert.Equal(sqlite.down.OfType<AlterColumnOperation>().Select(Key).Order(),
            mysql.down.OfType<AlterColumnOperation>().Select(Key).Order());
    }

    private static (IReadOnlyList<MigrationOperation> up, IReadOnlyList<MigrationOperation> down) Operations(Migration migration, string provider)
    {
        var type = migration.GetType();
        var up = new MigrationBuilder(provider);
        var down = new MigrationBuilder(provider);
        type.GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(migration, [up]);
        type.GetMethod("Down", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(migration, [down]);
        return (up.Operations, down.Operations);
    }
}
