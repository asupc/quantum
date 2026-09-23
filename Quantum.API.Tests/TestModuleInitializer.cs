using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quantum.Application;
using Quantum.Data;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 测试装配初始化：
/// 1. 关闭 SystemConfigHelper 对全局 Consts（JWT 密钥）的自动刷新。
///    SendMessageHelper 的静态消息泵会高频调用 GetSetting()（读 bin 目录下的
///    appsettings.json），其 Consts 刷新会与验签类用例的密钥窗口竞态（表现为偶发 401）。
///    生产环境默认开启（密钥热更新语义不变），仅测试装配内关闭。
/// 2. 装配 TaskPluginHost（任务执行引擎的静态 DI 桥）：进程内存 SQLite 每执行独立 scope，
///    与生产 Startup.Configure 的语义对齐。模块初始化器先于任何测试运行，
///    避免并行测试类重复 Configure 造成执行中途换 provider 的竞态。
/// </summary>
internal static class TestModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        SystemConfigHelper.ConstsAutoRefresh = false;
        // EnvService 等服务构造时取 NETCoreRepository logger：模块初始化即建仓（幂等），
        // 避免执行器经 TaskPluginHost 解析服务时才炸 Repository is NOT defined
        Contract.Log4NetTestSetup.EnsureRepository();
        TaskPluginHost.Configure(BuildTaskHostProvider());
    }

    private static IServiceProvider BuildTaskHostProvider()
    {
        // 连接静态存活（不关闭即整库存活）；每个 scope 各建 DbContext，EnsureCreated 幂等
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<QuantumSqliteDbContext>().UseSqlite(connection).Options;
        using (var bootstrap = new QuantumSqliteDbContext(options))
        {
            bootstrap.Database.EnsureCreated();
        }

        var services = new ServiceCollection();
        services.AddScoped<IQuantumDbContext>(_ =>
        {
            var db = new QuantumSqliteDbContext(options);
            db.Database.EnsureCreated();
            return db;
        });
        services.AddScoped<EnvService>();
        services.AddScoped<NotifyService>();
        services.AddScoped<CustomDataService>();
        services.AddScoped<CustomDataTitleService>();
        return services.BuildServiceProvider();
    }
}
