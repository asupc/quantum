using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Quantum.Data;

namespace Quantum.Data.DesignTime;

public class SqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<QuantumSqliteDbContext>
{
    public QuantumSqliteDbContext CreateDbContext(string[] args)
    {
        // SQLite 设计时使用固定的数据库路径
        var connectionString = "Data Source=db/quantum.db";

        var optionsBuilder = new DbContextOptionsBuilder<QuantumSqliteDbContext>();
        optionsBuilder.UseSqlite(connectionString);

        return new QuantumSqliteDbContext(optionsBuilder.Options);
    }
}
