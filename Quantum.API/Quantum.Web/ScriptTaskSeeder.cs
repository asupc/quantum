using Quantum.Application;
using Quantum.Data;
using Quantum.Utils;

namespace Quantum.Web;

/// <summary>
/// 任务脚本播种（Startup 建库/迁移完成后调用一次）：按首次部署初始化向导的状态决定播种范围——
/// 1) 向导未完成（SetupCompleted=false）→ 不播种，勾选结果由向导完成时直接建到当前库；
/// 2) 有向导待办（SetupPendingScripts != null）→ 按清单在当前库补种（换库重启后的落点），完成后清待办；
/// 3) 无待办且数据库为本次新建 → 全量播种（存量零配置行为不变）；其余不动。
/// </summary>
public static class ScriptTaskSeeder
{
    /// <summary>启动播种入口（Startup.Configure 调用）：sourceDirectory/runtimeDirectory 省略时取默认目录。</summary>
    public static void RunAfterInit(IServiceProvider services, bool dbJustCreated,
        string sourceDirectory = null, string runtimeDirectory = null)
    {
        var setting = SystemConfigHelper.GetSetting();
        if (setting.SetupCompleted == false)
        {
            return;
        }
        var pending = setting.SetupPendingScripts;
        if (pending == null && !dbJustCreated)
        {
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IQuantumDbContext>();
        var seeder = new SetupService(db,
            sourceDirectory ?? SetupService.DefaultScriptSourceDirectory,
            runtimeDirectory ?? SetupService.DefaultScriptRuntimeDirectory);

        if (pending != null)
        {
            try
            {
                var names = pending.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                seeder.SeedSelectedTasks(names);
                SystemConfigHelper.UpdateSetting(current => current.SetupPendingScripts = null);
            }
            catch (Exception e)
            {
                // 向导勾选已在完成时校验过，此处失败多为运行目录/文件不可写等环境问题：
                // 保留待办下次启动自动重试，不因任务补种阻塞启动
                Console.WriteLine($"[ERROR] 初始化向导任务补种失败（配置未清理，下次启动自动重试）：{e.Message}");
            }
        }
        else
        {
            seeder.SeedSelectedTasks(null);
        }
    }

    /// <summary>全量播种：随应用分发的正式脚本全部登记为待配置任务（停用态）。</summary>
    public static void Seed(IQuantumDbContext db, string sourceDirectory, string runtimeDirectory)
        => new SetupService(db, sourceDirectory, runtimeDirectory).SeedSelectedTasks(null);
}
