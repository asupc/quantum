using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// AI Agent 会话与运行（管理员专用，2026-09-20 新增，AI 脚本修复 Agent 计划阶段三/四）。
/// 会话与任务会话完全分离；运行进度由前端轮询 runs/steps 呈现（不占长连接，nginx 无需改动）。
/// </summary>
[CustomAuthorizationFilter]
[ManagerOnly]
public class AiAgentController : BaseController
{
    private readonly AgentService _agentService;
    private readonly AiProviderService _aiProviderService;
    private readonly AgentProposalService _proposalService;

    public AiAgentController(AgentService agentService, AiProviderService aiProviderService, AgentProposalService proposalService)
    {
        _agentService = agentService;
        _aiProviderService = aiProviderService;
        _proposalService = proposalService;
    }

    /// <summary>
    /// 会话列表
    /// </summary>
    [HttpGet("conversations")]
    public Task<List<AiConversationModel>> GetConversations()
    {
        return _agentService.GetConversationsAsync();
    }

    /// <summary>
    /// 新建/改名会话（AllowEnvValues：本会话是否允许把环境变量值发给模型）
    /// </summary>
    [HttpPost("conversations")]
    public Task<AiConversationModel> SaveConversation([FromBody] AiConversationModel model)
    {
        return _agentService.SaveConversationAsync(model);
    }

    /// <summary>
    /// 删除会话（连带消息/运行/轨迹/提案）
    /// </summary>
    /// <param name="ids">会话 Id 列表（逗号拼接）</param>
    [HttpDelete("conversations/deletes")]
    [ActionLogFilter("删除AI会话")]
    public Task<bool> DeleteConversations([FromQuery] string ids)
    {
        return _agentService.DeleteConversationsAsync(SplitIds(ids));
    }

    /// <summary>
    /// 增量拉消息（afterSeq=0 拉最近 limit 条）
    /// </summary>
    [HttpGet("messages")]
    public Task<List<AiMessageModel>> GetMessages([FromQuery] string conversationId, [FromQuery] long afterSeq = 0, [FromQuery] int limit = 200)
    {
        return _agentService.GetMessagesAsync(conversationId, afterSeq, limit);
    }

    /// <summary>
    /// 发起一次运行（立即返回，后台跑工具循环；前端轮询 runs/steps 与 messages）
    /// </summary>
    [HttpPost("chat")]
    [ActionLogFilter("AI分析脚本")]
    public async Task<object> Chat([FromBody] AiChatRequest request)
    {
        var (run, message) = await _agentService.StartAsync(request?.ConversationId, request?.Content, request?.TargetFile);
        return new { RunId = run.Id, MessageId = message.Id };
    }

    /// <summary>
    /// 运行状态（含进度文案）
    /// </summary>
    [HttpGet("runs/{id}")]
    public async Task<AiRunView> GetRun([FromRoute] string id)
    {
        var run = await _agentService.GetRunAsync(id)
            ?? throw new Quantum.Utils.BusinessException("运行记录不存在，请刷新后重试！");
        return await BuildRunViewAsync(run);
    }

    /// <summary>
    /// 会话最近一次运行（任意状态；无 run 时 Data=null）——页面刷新/重进后的运行态恢复权威来源
    /// （字面量段优先于参数段，与 runs/{id} 无路由冲突）
    /// </summary>
    [HttpGet("runs/latest")]
    public async Task<AiRunView> GetLatestRun([FromQuery] string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new Quantum.Utils.BusinessException("会话 Id 不能为空！");
        }
        var run = await _agentService.GetLatestRunAsync(conversationId);
        return run == null ? null : await BuildRunViewAsync(run);
    }

    /// <summary>run → AiRunView 组装（进度文案取最后一步；GetRun 与 runs/latest 共用）</summary>
    private async Task<AiRunView> BuildRunViewAsync(AiRunModel run)
    {
        var steps = await _agentService.GetStepsAsync(run.Id);
        var last = steps.LastOrDefault();
        return new AiRunView
        {
            Id = run.Id,
            ConversationId = run.ConversationId,
            Status = run.Status,
            TargetFile = run.TargetFile,
            Model = run.Model,
            Rounds = run.Rounds,
            PromptTokens = run.PromptTokens,
            CompletionTokens = run.CompletionTokens,
            DurationMs = run.DurationMs,
            Error = run.Error,
            Progress = last == null
                ? (run.Status == AiRunStatus.Running ? "思考中…" : null)
                : last.Kind switch
                {
                    "tool_call" => $"调用 {last.Name}…",
                    "tool_result" => $"已完成 {last.Name}",
                    _ => "整理结论…"
                },
            CreateTime = run.CreateTime,
            FinishTime = run.FinishTime
        };
    }

    /// <summary>
    /// 停止运行
    /// </summary>
    [HttpPost("runs/{id}/cancel")]
    public Task<bool> CancelRun([FromRoute] string id)
    {
        return _agentService.CancelAsync(id);
    }

    /// <summary>
    /// 运行过程轨迹（工具调用/结果，页面过程面板）
    /// </summary>
    [HttpGet("runs/{id}/steps")]
    public Task<List<AiStepModel>> GetSteps([FromRoute] string id, [FromQuery] int afterSeq = 0)
    {
        return _agentService.GetStepsAsync(id, afterSeq);
    }

    /// <summary>
    /// 平台能力摘要（页面「AI 已知的平台能力」面板）
    /// </summary>
    [HttpGet("contract")]
    public string GetContract()
    {
        return ScriptContractCatalog.Describe();
    }

    /// <summary>
    /// 手动再试运行一次提案（影子执行，真实副作用）
    /// </summary>
    [HttpPost("proposals/{id}/test-run")]
    [ActionLogFilter("AI提案试运行")]
    public async Task<AiTestRunResult> TestRunProposal([FromRoute] string id)
    {
        var setting = await _aiProviderService.GetSettingAsync();
        return await _proposalService.TestRunAsync(id, setting);
    }

    /// <summary>
    /// 应用提案（服务端复校：基线哈希 + 门禁 + 编译；不过即拒绝且不改磁盘）
    /// </summary>
    [HttpPost("proposals/{id}/apply")]
    [ActionLogFilter("AI提案应用脚本")]
    public Task<ScriptBuildService.ScriptSaveResult> ApplyProposal([FromRoute] string id)
    {
        return _proposalService.ApplyAsync(id, GetUserId());
    }

    /// <summary>
    /// 忽略提案
    /// </summary>
    [HttpPost("proposals/{id}/discard")]
    [ActionLogFilter("AI提案忽略")]
    public Task<bool> DiscardProposal([FromRoute] string id)
    {
        return _proposalService.DiscardAsync(id);
    }

    /// <summary>
    /// 提案差异对照（基线内容 + 候选内容，前端做行级 diff）
    /// </summary>
    [HttpGet("proposals/{id}/content")]
    public Task<object> GetProposalContent([FromRoute] string id)
    {
        return _proposalService.GetContentAsync(id);
    }

    /// <summary>
    /// 当前默认模型（页面顶部选择器展示）
    /// </summary>
    [HttpGet("default-model")]
    public async Task<object> GetDefaultModel()
    {
        var (provider, model) = await _aiProviderService.GetDefaultModelAsync();
        return new
        {
            ProviderId = provider?.Id,
            ProviderName = provider?.Name,
            ModelId = model?.ModelId,
            DisplayName = model?.DisplayName,
            ContextWindow = model?.ContextWindow ?? 0,
            SupportsTools = provider?.SupportsTools ?? AiToolSupport.Unknown
        };
    }

    private static List<string> SplitIds(string ids)
    {
        return (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
