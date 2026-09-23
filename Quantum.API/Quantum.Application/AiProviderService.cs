using System.Diagnostics;
using log4net;
using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// AI 供应商与模型配置服务（2026-09-20 新增，AI 脚本修复 Agent 计划阶段二）：
/// 供应商/模型 CRUD、连通性与工具能力探测、模型清单拉取导入、全局 AI 设置（单行）。
/// 密钥明文入库但对外只回掩码；明文只在显式 reveal 动作里返回（控制器记操作日志）。
/// </summary>
public class AiProviderService
{
    private static readonly ILog _log = LogManager.GetLogger("NETCoreRepository", typeof(AiProviderService));

    readonly IQuantumDbContext _dbContext;
    readonly ILlmClient _llmClient;

    public AiProviderService(IQuantumDbContext dbContext, ILlmClient llmClient)
    {
        _dbContext = dbContext;
        _llmClient = llmClient;
    }

    /// <summary>
    /// BaseUrl 规范化：去尾部斜杠；只有「裸域名」才补 /v1
    /// （已带路径段的（如智谱 /api/paas/v4）原样使用，不猜用户意图）。
    /// </summary>
    public static string NormalizeBaseUrl(string url)
    {
        var value = (url ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return value;
        }
        value = value.TrimEnd('/');
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        var hostStart = schemeEnd < 0 ? 0 : schemeEnd + 3;
        return value.IndexOf('/', hostStart) < 0 ? value + "/v1" : value;
    }

    /// <summary>密钥掩码：保留首 4 位与末 4 位，中间打星（过短的整段打星）。</summary>
    public static string MaskKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }
        if (key.Length <= 12)
        {
            return "****";
        }
        return $"{key[..4]}****{key[^4..]}";
    }

    // ==================================================================== 供应商

    /// <summary>供应商列表（含各自模型；密钥只回掩码）。</summary>
    public async Task<List<AiProviderItem>> GetProvidersAsync()
    {
        var providers = await _dbContext.AiProviders.AsNoTracking()
            .OrderBy(n => n.Sort).ThenBy(n => n.CreateTime).ToListAsync();
        var models = await _dbContext.AiModels.AsNoTracking()
            .OrderBy(n => n.Sort).ThenBy(n => n.CreateTime).ToListAsync();
        return providers.Select(n => ToItem(n, models.Where(m => m.ProviderId == n.Id).ToList())).ToList();
    }

    private static AiProviderItem ToItem(AiProviderModel provider, List<AiModelModel> models) => new()
    {
        Id = provider.Id,
        Name = provider.Name,
        BaseUrl = provider.BaseUrl,
        Protocol = AiProtocol.Normalize(provider.Protocol),
        ApiKeyMasked = MaskKey(provider.ApiKey),
        HasApiKey = !string.IsNullOrWhiteSpace(provider.ApiKey),
        UsePlatformProxy = provider.UsePlatformProxy,
        TimeoutSeconds = provider.TimeoutSeconds,
        MaxRetries = provider.MaxRetries,
        Enable = provider.Enable,
        Sort = provider.Sort,
        SupportsTools = provider.SupportsTools,
        LastTestTime = provider.LastTestTime,
        LastTestOk = provider.LastTestOk,
        LastTestMessage = provider.LastTestMessage,
        CreateTime = provider.CreateTime,
        UpdateTime = provider.UpdateTime,
        Models = models
    };

    /// <summary>按 Id 取供应商实体（不存在抛业务异常）。</summary>
    public async Task<AiProviderModel> GetEntityAsync(string id)
    {
        var provider = await _dbContext.AiProviders.FirstOrDefaultAsync(n => n.Id == id);
        if (provider == null)
        {
            throw new BusinessException("AI 供应商不存在，请刷新后重试！");
        }
        return provider;
    }

    /// <summary>新增/修改供应商（ApiKey 留空表示保持原密钥不变）。</summary>
    public async Task<AiProviderItem> SaveProviderAsync(AiProviderSaveModel model)
    {
        if (model == null || string.IsNullOrWhiteSpace(model.Name))
        {
            throw new BusinessException("供应商名称不能为空！");
        }
        var baseUrl = NormalizeBaseUrl(model.BaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new BusinessException("BaseUrl 必须是 http/https 开头的合法地址！");
        }

        // 新增/修改按「库中是否存在」判定：BaseModel.Id 有属性初始化器，JSON 绑定出来的新对象
        // 也带着一个随机 GUID，不能拿 Id 是否为空当新增标志（否则新建会被当成更新而报「不存在」）
        var provider = string.IsNullOrWhiteSpace(model.Id)
            ? null
            : await _dbContext.AiProviders.FirstOrDefaultAsync(n => n.Id == model.Id);
        var isNew = provider == null;
        if (isNew)
        {
            provider = new AiProviderModel { CreateTime = DateTime.Now };
            _dbContext.AiProviders.Add(provider);
        }
        provider.Name = model.Name.Trim();
        provider.BaseUrl = baseUrl;
        provider.Protocol = AiProtocol.Normalize(model.Protocol);
        provider.UsePlatformProxy = model.UsePlatformProxy;
        provider.TimeoutSeconds = Math.Clamp(model.TimeoutSeconds, 10, 600);
        provider.MaxRetries = Math.Clamp(model.MaxRetries, 0, 5);
        provider.Enable = model.Enable;
        provider.Sort = model.Sort;
        if (model.SupportsTools >= 0)
        {
            provider.SupportsTools = model.SupportsTools;
        }
        if (!string.IsNullOrWhiteSpace(model.ApiKey))
        {
            provider.ApiKey = model.ApiKey.Trim();
        }
        if (isNew && string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new BusinessException("新增供应商必须填写 API 密钥！");
        }
        provider.UpdateTime = DateTime.Now;
        await _dbContext.SaveChangesAsync();

        var models = await _dbContext.AiModels.AsNoTracking().Where(n => n.ProviderId == provider.Id).ToListAsync();
        return ToItem(provider, models);
    }

    /// <summary>删除供应商（连带其模型）。</summary>
    public async Task<bool> DeleteAsync(List<string> ids)
    {
        if (ids == null || ids.Count == 0)
        {
            return false;
        }
        var providers = await _dbContext.AiProviders.Where(n => ids.Contains(n.Id)).ToListAsync();
        if (providers.Count == 0)
        {
            return false;
        }
        var providerIds = providers.Select(n => n.Id).ToList();
        var models = await _dbContext.AiModels.Where(n => providerIds.Contains(n.ProviderId)).ToListAsync();
        _dbContext.AiModels.RemoveRange(models);
        _dbContext.AiProviders.RemoveRange(providers);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    /// <summary>返回明文密钥（控制器须记操作日志：这是唯一的明文出口）。</summary>
    public async Task<string> RevealKeyAsync(string id)
    {
        var provider = await GetEntityAsync(id);
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new BusinessException("该供应商尚未配置 API 密钥！");
        }
        return provider.ApiKey;
    }

    // ==================================================================== 连通性测试

    /// <summary>
    /// 连通性测试三步：①拉 /models 清单（失败不致命）②最小对话请求 ③工具能力探测。
    /// 结论写回供应商（SupportsTools / LastTest*），供 Agent 运行时决定工具协议。
    /// </summary>
    public async Task<AiTestResult> TestAsync(string id, string modelId = null)
    {
        var provider = await GetEntityAsync(id);
        var result = new AiTestResult();
        var watch = Stopwatch.StartNew();

        // ① 模型清单（部分供应商无此端点，失败只提示不判失败）
        string listError = null;
        try
        {
            result.Models = await _llmClient.ListModelsAsync(provider);
        }
        catch (Exception e)
        {
            listError = e.Message;
        }

        var model = modelId;
        if (string.IsNullOrWhiteSpace(model))
        {
            model = await _dbContext.AiModels.AsNoTracking()
                .Where(n => n.ProviderId == provider.Id && n.Enable)
                .OrderBy(n => n.Sort).ThenBy(n => n.CreateTime)
                .Select(n => n.ModelId).FirstOrDefaultAsync();
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            model = result.Models.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            result.Ok = result.Models.Count > 0;
            result.SupportsTools = provider.SupportsTools;
            result.Message = result.Ok
                ? $"已连通：拉到 {result.Models.Count} 个模型，但尚未配置可用模型，无法验证对话"
                : $"连接失败：{listError ?? "未配置模型且供应商未提供模型清单"}";
        }
        else
        {
            try
            {
                var chat = await _llmClient.ChatAsync(new LlmChatRequest
                {
                    Provider = provider,
                    Model = model,
                    Temperature = 0,
                    // 探测预算不能太小：推理型模型（DeepSeek-R1/GLM 等）会先输出 reasoning_content，
                    // 32 个 token 会被思考吃光导致 content 为空、finish_reason=length（服务其实是通的）
                    MaxOutputTokens = 256,
                    Messages = [LlmMessage.User("ping，请回复 pong")]
                });
                result.Ok = true;
                result.Message = $"连通正常（模型 {model}，{chat.LatencyMs} ms"
                    + (result.Models.Count > 0 ? $"，可用模型 {result.Models.Count} 个" : "，供应商无模型清单端点")
                    + "）";
                result.SupportsTools = await ProbeToolsAsync(provider, model);
            }
            catch (LlmException e) when (e.Truncated)
            {
                // 被输出上限截断说明端点可达、鉴权有效，只是该模型在探测预算内只产出了思考内容
                result.Ok = true;
                result.Message = $"已连通（模型 {model}）；{e.Message}";
                result.SupportsTools = await ProbeToolsAsync(provider, model);
            }
            catch (Exception e)
            {
                result.Ok = false;
                result.Message = e.Message;
            }
        }

        result.LatencyMs = watch.ElapsedMilliseconds;
        provider.SupportsTools = result.SupportsTools;
        provider.LastTestTime = DateTime.Now;
        provider.LastTestOk = result.Ok;
        provider.LastTestMessage = result.Message?.Length > 500 ? result.Message[..500] : result.Message;
        provider.UpdateTime = DateTime.Now;
        await _dbContext.SaveChangesAsync();
        return result;
    }

    /// <summary>
    /// 工具能力探测：带一个 dummy 工具并要求模型调用。
    /// 返回 tool_calls → 支持；400 且报文提到 tool → 不支持；其余（模型没调但正常回答）→ 保持未知。
    /// </summary>
    private async Task<int> ProbeToolsAsync(AiProviderModel provider, string model)
    {
        try
        {
            var probe = await _llmClient.ChatAsync(new LlmChatRequest
            {
                Provider = provider,
                Model = model,
                Temperature = 0,
                MaxOutputTokens = 256,
                Messages = [LlmMessage.User("请调用 get_time 工具查询时间，必须通过工具调用完成，不要直接回答。")],
                Tools =
                [
                    new LlmTool
                    {
                        Name = "get_time",
                        Description = "获取指定时区的当前时间",
                        ParametersJson = """{"type":"object","properties":{"zone":{"type":"string","description":"时区，如 Asia/Shanghai"}},"required":["zone"]}"""
                    }
                ]
            });
            return probe.HasToolCalls ? AiToolSupport.Supported : AiToolSupport.Unknown;
        }
        catch (LlmException e)
        {
            if (e.StatusCode == 400 && e.Message.Contains("tool", StringComparison.OrdinalIgnoreCase))
            {
                return AiToolSupport.Unsupported;
            }
            _log.Warn($"工具能力探测失败（{provider.Name}/{model}）：{e.Message}");
            return AiToolSupport.Unknown;
        }
        catch (Exception e)
        {
            _log.Warn($"工具能力探测异常（{provider.Name}/{model}）：{e.Message}");
            return AiToolSupport.Unknown;
        }
    }

    /// <summary>拉取供应商模型清单（供页面勾选导入；已存在的也返回，页面标灰）。</summary>
    public async Task<List<string>> SyncModelsAsync(string providerId)
    {
        var provider = await GetEntityAsync(providerId);
        return await _llmClient.ListModelsAsync(provider);
    }

    /// <summary>导入勾选的模型（已存在的跳过；保留既有上下文大小等配置）。</summary>
    public async Task<int> ImportModelsAsync(AiModelImportModel model)
    {
        if (model == null || string.IsNullOrWhiteSpace(model.ProviderId))
        {
            throw new BusinessException("未指定供应商！");
        }
        await GetEntityAsync(model.ProviderId);
        var ids = (model.ModelIds ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct().ToList();
        if (ids.Count == 0)
        {
            return 0;
        }
        var existing = await _dbContext.AiModels
            .Where(n => n.ProviderId == model.ProviderId && ids.Contains(n.ModelId))
            .Select(n => n.ModelId).ToListAsync();
        var added = 0;
        foreach (var id in ids.Where(n => !existing.Contains(n)))
        {
            _dbContext.AiModels.Add(new AiModelModel
            {
                ProviderId = model.ProviderId,
                ModelId = id,
                DisplayName = id,
                CreateTime = DateTime.Now
            });
            added++;
        }
        if (added > 0)
        {
            await _dbContext.SaveChangesAsync();
        }
        return added;
    }

    // ==================================================================== 模型

    /// <summary>新增/修改模型（含上下文大小、输出上限、温度）。</summary>
    public async Task<AiModelModel> SaveModelAsync(AiModelModel model)
    {
        if (model == null || string.IsNullOrWhiteSpace(model.ModelId))
        {
            throw new BusinessException("模型标识不能为空！");
        }
        if (string.IsNullOrWhiteSpace(model.ProviderId))
        {
            throw new BusinessException("未指定供应商！");
        }
        await GetEntityAsync(model.ProviderId);

        // 同供应商：新增/修改按存在性判定（理由见 SaveProviderAsync）
        var entity = string.IsNullOrWhiteSpace(model.Id)
            ? null
            : await _dbContext.AiModels.FirstOrDefaultAsync(n => n.Id == model.Id);
        if (entity == null)
        {
            entity = new AiModelModel { CreateTime = DateTime.Now };
            _dbContext.AiModels.Add(entity);
        }
        entity.ProviderId = model.ProviderId;
        entity.ModelId = model.ModelId.Trim();
        entity.DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? entity.ModelId : model.DisplayName.Trim();
        entity.ContextWindow = Math.Clamp(model.ContextWindow, 1024, 2000000);
        entity.MaxOutputTokens = Math.Clamp(model.MaxOutputTokens, 64, 200000);
        entity.Temperature = Math.Clamp(model.Temperature, 0, 2);
        entity.Enable = model.Enable;
        entity.Sort = model.Sort;
        entity.UpdateTime = DateTime.Now;
        await _dbContext.SaveChangesAsync();
        if (model.IsDefault)
        {
            await SetDefaultModelAsync(entity.Id);
        }
        return entity;
    }

    /// <summary>设为默认模型（全局仅一个：先清空其它模型的默认标记）。</summary>
    public async Task<bool> SetDefaultModelAsync(string id)
    {
        var models = await _dbContext.AiModels.ToListAsync();
        var target = models.FirstOrDefault(n => n.Id == id)
            ?? throw new BusinessException("模型不存在，请刷新后重试！");
        foreach (var model in models)
        {
            model.IsDefault = model.Id == target.Id;
        }
        target.Enable = true;
        target.UpdateTime = DateTime.Now;
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteModelsAsync(List<string> ids)
    {
        if (ids == null || ids.Count == 0)
        {
            return false;
        }
        var models = await _dbContext.AiModels.Where(n => ids.Contains(n.Id)).ToListAsync();
        if (models.Count == 0)
        {
            return false;
        }
        _dbContext.AiModels.RemoveRange(models);
        await _dbContext.SaveChangesAsync();
        // 默认模型被删后自动指定一个可用模型，避免 Agent 无模型可用
        if (!await _dbContext.AiModels.AnyAsync(n => n.IsDefault))
        {
            var fallback = await _dbContext.AiModels.Where(n => n.Enable)
                .OrderBy(n => n.Sort).ThenBy(n => n.CreateTime).FirstOrDefaultAsync();
            if (fallback != null)
            {
                fallback.IsDefault = true;
                await _dbContext.SaveChangesAsync();
            }
        }
        return true;
    }

    /// <summary>
    /// 取当前默认模型（含供应商）；未配置默认模型时回退到「第一个启用模型 + 其启用的供应商」。
    /// Agent 运行时与页面顶部模型选择器都用它。
    /// </summary>
    public async Task<(AiProviderModel Provider, AiModelModel Model)> GetDefaultModelAsync()
    {
        var providers = await _dbContext.AiProviders.AsNoTracking().Where(n => n.Enable).ToListAsync();
        if (providers.Count == 0)
        {
            return (null, null);
        }
        var providerIds = providers.Select(n => n.Id).ToList();
        var models = await _dbContext.AiModels.AsNoTracking()
            .Where(n => n.Enable && providerIds.Contains(n.ProviderId))
            .OrderByDescending(n => n.IsDefault).ThenBy(n => n.Sort).ThenBy(n => n.CreateTime)
            .ToListAsync();
        var model = models.FirstOrDefault();
        if (model == null)
        {
            return (null, null);
        }
        return (providers.First(n => n.Id == model.ProviderId), model);
    }

    // ==================================================================== 全局设置

    /// <summary>取全局 AI 设置（单行；首次访问自动建默认行）。</summary>
    public async Task<AiSettingModel> GetSettingAsync()
    {
        var setting = await _dbContext.AiSettings.FirstOrDefaultAsync(n => n.Id == AiSettingModel.DefaultId);
        if (setting != null)
        {
            return setting;
        }
        setting = new AiSettingModel { Id = AiSettingModel.DefaultId, CreateTime = DateTime.Now };
        _dbContext.AiSettings.Add(setting);
        await _dbContext.SaveChangesAsync();
        return setting;
    }

    public async Task<AiSettingModel> SaveSettingAsync(AiSettingModel model)
    {
        var setting = await GetSettingAsync();
        setting.Enable = model.Enable;
        setting.MaxRounds = Math.Clamp(model.MaxRounds, 1, 100);
        setting.RunTimeoutSeconds = Math.Clamp(model.RunTimeoutSeconds, 60, 3000);
        setting.LogTailLines = Math.Clamp(model.LogTailLines, 20, 2000);
        setting.AutoAnalyzeOnFailure = model.AutoAnalyzeOnFailure;
        setting.TestRunNotifyMode = model.TestRunNotifyMode is AiTestRunNotifyMode.Task or AiTestRunNotifyMode.Force or AiTestRunNotifyMode.Silent
            ? model.TestRunNotifyMode
            : AiTestRunNotifyMode.Task;
        setting.TestRunRequireConfirm = model.TestRunRequireConfirm;
        setting.KeepVersionsPerFile = Math.Clamp(model.KeepVersionsPerFile, 1, 500);
        setting.SaveFullPrompt = model.SaveFullPrompt;
        setting.SystemPromptExtra = string.IsNullOrWhiteSpace(model.SystemPromptExtra) ? null : model.SystemPromptExtra.Trim();
        // AI 写权限四开关（默认全关；bool 直赋与上方开关同款）——保存即对下一次发起的对话生效
        setting.AllowScriptDelete = model.AllowScriptDelete;
        setting.AllowTaskManage = model.AllowTaskManage;
        setting.AllowEnvManage = model.AllowEnvManage;
        setting.AllowCustomDataManage = model.AllowCustomDataManage;
        setting.UpdateTime = DateTime.Now;
        await _dbContext.SaveChangesAsync();
        return setting;
    }
}
