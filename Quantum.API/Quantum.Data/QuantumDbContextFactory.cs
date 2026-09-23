using Quantum.Utils;

namespace Quantum.Data;

/// <summary>
/// 短生命周期 DbContext 工厂：按配置在 SQLite/MySQL 间选择实现。
/// 供不处于 HTTP 请求作用域的后台任务（业务日志落库、缓存全表加载等）按批创建上下文；
/// 请求路径内一律注入 IQuantumDbContext，禁止手工创建。
/// </summary>
public static class QuantumDbContextFactory
{
    public static IQuantumDbContext Create()
    {
        return SystemConfigHelper.GetSetting().DBType.ToLower() == "sqlite"
            ? new QuantumSqliteDbContext()
            : new QuantumMySqlDbContext();
    }
}
