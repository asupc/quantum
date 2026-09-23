using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Quantum.Data.DesignTime;

public class MySqlDesignTimeDbContextFactory : IDesignTimeDbContextFactory<QuantumMySqlDbContext>
{
    public QuantumMySqlDbContext CreateDbContext(string[] args)
    {
        // MySQL 设计时连接字符串 - 需要根据实际情况修改
        // 格式: Server=localhost;Port=3306;Database=quantum;User=root;Password=xxx;
        var connectionString = "Server=127.0.0.1;Port=3306;Database=quantum;User=root;Password=root;";

        var optionsBuilder = new DbContextOptionsBuilder<QuantumMySqlDbContext>();
        optionsBuilder.UseMySQL(connectionString, null);

        return new QuantumMySqlDbContext(optionsBuilder.Options);
    }
}
