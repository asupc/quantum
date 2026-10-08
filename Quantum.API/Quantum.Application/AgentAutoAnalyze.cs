using log4net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quantum.Data;
using Quantum.Entities.Model;

namespace Quantum.Application;

/// <summary>
/// 任务失败 → AI 自动分析接线（2026-09-20 新增，AI 脚本修复 Agent 计划阶段五）。
/// 默认**不自动跑模型**：仅当全局设置里打开「失败自动分析」时才在默认会话里起一次运行，
/// 避免每次抖动都消耗模型调用；未打开时沿用既有的任务失败站内通知（不再重复推一条 AI 通知）。
/// 与 TaskPluginHost 同款静态桥：静态执行链（TaskExcuteService/TaskService）经 Configure 注入的
/// 容器为每次触发创建独立 DI scope。
/// </summary>
public static class AgentAutoAnalyze
{
    private static readonly ILog _log = LogManager.GetLogger("NETCoreRepository", typeof(AgentAutoAnalyze));

    private static IServiceProvider _services;

    public static void Configure(IServiceProvider services) => _services = services;

    /// <summary>
    /// 任务执行异常后的入口（fire-and-forget）。入参是失败任务的信息，内部自行判定开关与是否值得分析。
    /// </summary>
    public static void OnTaskFailure(string taskId, string taskName, string fileName, string error)
    {
        if (_services == null || string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _services.CreateScope();
                var provider = scope.ServiceProvider.GetRequiredService<AiProviderService>();
                var setting = await provider.GetSettingAsync();
                if (!setting.Enable || !setting.AutoAnalyzeOnFailure)
                {
                    return;
                }
                var (aiProvider, aiModel) = await provider.GetDefaultModelAsync();
                if (aiProvider == null || aiModel == null)
                {
                    return;
                }
                var agent = scope.ServiceProvider.GetRequiredService<AgentService>();
                var conversation = await agent.EnsureDefaultConversationAsync();
                var skipReason = await SkipReasonAsync(
                    scope.ServiceProvider.GetRequiredService<IQuantumDbContext>(), conversation.Id, fileName);
                if (skipReason != null)
                {
                    _log.Info($"跳过任务「{taskName}」失败自动分析：{skipReason}");
                    return;
                }
                var content = $"""
                    任务「{taskName}」执行失败，脚本是 {fileName}。
                    报错信息：{error}

                    请读它的最近执行日志定位根因，改好后先编译校验并试运行，再给我提案。
                    """;
                await agent.StartAsync(conversation.Id, content, fileName, AiRunTrigger.AutoFailure);
                _log.Info($"已按「失败自动分析」开关为任务「{taskName}」发起 AI 分析");
                LogServiceHelper.Info("任务失败自动分析已发起", $"任务「{taskName}」脚本 {fileName} 已在默认会话发起 AI 分析",
                    "AI", "AI", LogType.AI助手);
            }
            catch (Exception e)
            {
                _log.Error($"任务失败自动分析发起失败（{taskName}）", e);
            }
        });
    }

    /// <summary>
    /// 防重判定（2026-10-08 修复）：修复提案是「等用户应用」的挂起态——没应用前任务每次执行仍会失败，
    /// 若不拦截，每次失败都会再起一轮自动分析，重复烧模型调用、堆重复提案。
    /// 目标脚本存在待确认提案（Pending），或默认会话已有运行中的 run（手动/自动均占）时跳过；
    /// 提案被应用/忽略/判过期后即不再拦截，下一次失败可恢复正常触发。
    /// </summary>
    internal static async Task<string> SkipReasonAsync(IQuantumDbContext db, string conversationId, string fileName)
    {
        if (await db.AiProposals.AsNoTracking().AnyAsync(n => n.FileName == fileName && n.Status == AiProposalStatus.Pending))
        {
            return $"脚本 {fileName} 已有待确认的 AI 修复提案（应用/忽略前不重复分析）";
        }
        if (await db.AiRuns.AsNoTracking().AnyAsync(n => n.ConversationId == conversationId && n.Status == AiRunStatus.Running))
        {
            return "默认会话已有运行中的 AI run";
        }
        return null;
    }

    /// <summary>
    /// 启动清理（进程重启残留）：中断的 run 置失败、试运行影子文件删除、过期提案清理。
    /// </summary>
    public static void StartupCleanup(IServiceProvider services)
    {
        AgentService.RecoverInterrupted(services);
        AgentTestRunService.CleanStaging();
        try
        {
            using var scope = services.CreateScope();
            var proposals = scope.ServiceProvider.GetRequiredService<AgentProposalService>();
            var removed = proposals.CleanupAsync().GetAwaiter().GetResult();
            if (removed > 0)
            {
                _log.Info($"启动清理：删除 {removed} 条过期 AI 提案");
            }
        }
        catch (Exception e)
        {
            _log.Error("启动清理 AI 提案失败", e);
        }
    }
}
