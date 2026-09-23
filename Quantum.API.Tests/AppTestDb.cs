using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Quantum.Data;

namespace Quantum.API.Tests;

/// <summary>
/// App 通道单测共享：进程内存 SQLite（连接不关闭即整库存活），EnsureCreated 按当前模型建表。
/// </summary>
internal static class AppTestDb
{
    public static (SqliteConnection connection, QuantumSqliteDbContext db) Create()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<QuantumSqliteDbContext>().UseSqlite(connection).Options;
        var db = new QuantumSqliteDbContext(options);
        db.Database.EnsureCreated();
        return (connection, db);
    }
}
