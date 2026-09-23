using Microsoft.Extensions.DependencyInjection;
using Quantum.Entities.Model;
using Quartz;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Quantum.Application;

/// <summary>
/// Quartz 任务注册辅助。
/// 调度器由 DI 统一提供（Startup.AddQuartz + QuartzHostedService 托管启停），
/// JobHelper 启动时经 <see cref="Init"/> 拿到同一个 ISchedulerFactory——
/// 历史上这里自建 StandaloneSchedulerFactory 形成双调度器：托管服务监控/优雅停机全部落空。
/// JobDataMap 只存 TaskId，不再存活体实体/Scoped 服务（状态以数据库与缓存为准）。
/// </summary>
public static class JobHelper
{
    private static ISchedulerFactory _schedulerFactory;

    public static void Init(ISchedulerFactory schedulerFactory)
    {
        _schedulerFactory = schedulerFactory;
    }

    private static async Task<IScheduler> GetSchedulerAsync()
    {
        if (_schedulerFactory == null)
        {
            throw new InvalidOperationException("Quartz 调度器未初始化，请确认 JobHelper.Init 已在启动时调用。");
        }
        return await _schedulerFactory.GetScheduler();
    }

    public static async Task CreateQuartzJob(this TaskModel taskInfo)
    {
        if (!taskInfo.Enable || string.IsNullOrEmpty(taskInfo.Cron) || !CronExpression.TryParse(taskInfo.Cron, out _))
        {
            return;
        }
        IScheduler scheduler = await GetSchedulerAsync();
        if (scheduler.Status != SchedulerStatus.Running)
        {
            await scheduler.Start();
        }
        string jobId = taskInfo.Id;
        var jobGroupName = taskInfo.FileName;
        var jobTriggerName = $"{jobId}_Trigger";
        var jobDataMap = new JobDataMap();
        jobDataMap["TaskId"] = taskInfo.Id;
        IJobDetail job = JobBuilder.Create<TaskJob>()
            .UsingJobData(jobDataMap)
            .WithIdentity(jobId, jobGroupName)
            .Build();
        ITrigger trigger = TriggerBuilder.Create()
            .WithIdentity(jobTriggerName, jobGroupName)
            .StartNow()
            .WithCronSchedule(taskInfo.Cron)
            .Build();
        await scheduler.ScheduleJob(job, trigger);
    }

    public static async Task DeleteQuartzJob(this TaskModel taskInfo)
    {
        IScheduler scheduler = await GetSchedulerAsync();
        var jobKey = new JobKey(taskInfo.Id, taskInfo.FileName);
        await scheduler.PauseJob(jobKey);
        // Trigger 必须带与创建时一致的组名（FileName），否则永远解绑不到
        await scheduler.UnscheduleJob(new TriggerKey($"{taskInfo.Id}_Trigger", taskInfo.FileName));
        await scheduler.DeleteJob(jobKey);
    }

    public static void Update(string s)
    {
#if !DEBUG
        Task.Run(() =>
        {
            Thread.Sleep(1000);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var file = "./config/task.ini";
                if (!File.Exists(file))
                {
                    File.Create(file).Close();
                }
                using (StreamWriter writer = new StreamWriter(file, false, Encoding.Default))
                {
                    writer.Write(s);
                    writer.Flush();
                }
            }
            else
            {
                Process.Start(s == "更新" ? "update-windows.bat" : "restart-window.bat");
            }
        });
#endif
    }
}
