using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// AI 供应商与模型配置（管理员专用，2026-09-20 新增）：
/// 供应商/模型 CRUD、连通性与工具能力测试、模型清单拉取导入、全局 AI 设置。
/// 密钥默认只回掩码；明文仅经 {id}/reveal 显式动作返回（记操作日志）。
/// </summary>
[CustomAuthorizationFilter]
[ManagerOnly]
public class AiProviderController : BaseController
{
    private readonly AiProviderService _aiProviderService;

    public AiProviderController(AiProviderService aiProviderService)
    {
        _aiProviderService = aiProviderService;
    }

    /// <summary>
    /// 供应商列表（含各自模型；密钥掩码）
    /// </summary>
    [HttpGet]
    public Task<List<AiProviderItem>> Get()
    {
        return _aiProviderService.GetProvidersAsync();
    }

    /// <summary>
    /// 新增/修改供应商（ApiKey 留空表示不改）
    /// </summary>
    [HttpPost]
    [ActionLogFilter("保存AI供应商")]
    public Task<AiProviderItem> Save([FromBody] AiProviderSaveModel model)
    {
        return _aiProviderService.SaveProviderAsync(model);
    }

    /// <summary>
    /// 删除供应商（连带其模型）
    /// </summary>
    /// <param name="ids">供应商 Id 列表（逗号拼接）</param>
    [HttpDelete("deletes")]
    [ActionLogFilter("删除AI供应商")]
    public Task<bool> Deletes([FromQuery] string ids)
    {
        return _aiProviderService.DeleteAsync(SplitIds(ids));
    }

    /// <summary>
    /// 显示明文密钥（唯一的明文出口，记操作日志）
    /// </summary>
    /// <param name="id">供应商 Id</param>
    [HttpPost("{id}/reveal")]
    [ActionLogFilter("查看AI供应商密钥")]
    public Task<string> Reveal([FromRoute] string id)
    {
        return _aiProviderService.RevealKeyAsync(id);
    }

    /// <summary>
    /// 连通性测试（拉模型清单 + 最小对话 + 工具能力探测，结论写回供应商）
    /// </summary>
    /// <param name="id">供应商 Id</param>
    /// <param name="modelId">指定测试模型（留空则用默认/第一个启用模型）</param>
    [HttpPost("{id}/test")]
    [ActionLogFilter("测试AI供应商")]
    public Task<AiTestResult> Test([FromRoute] string id, [FromQuery] string modelId)
    {
        return _aiProviderService.TestAsync(id, modelId);
    }

    /// <summary>
    /// 拉取供应商模型清单（页面勾选后经 {id}/models/import 导入）
    /// </summary>
    /// <param name="id">供应商 Id</param>
    [HttpGet("{id}/models")]
    public Task<List<string>> SyncModels([FromRoute] string id)
    {
        return _aiProviderService.SyncModelsAsync(id);
    }

    /// <summary>
    /// 导入勾选的模型
    /// </summary>
    [HttpPost("models/import")]
    [ActionLogFilter("导入AI模型")]
    public Task<int> ImportModels([FromBody] AiModelImportModel model)
    {
        return _aiProviderService.ImportModelsAsync(model);
    }

    /// <summary>
    /// 新增/修改模型（上下文大小、输出上限、温度、默认模型）
    /// </summary>
    [HttpPut("models")]
    [ActionLogFilter("保存AI模型")]
    public Task<AiModelModel> SaveModel([FromBody] AiModelModel model)
    {
        return _aiProviderService.SaveModelAsync(model);
    }

    /// <summary>
    /// 设为默认模型（全局仅一个）
    /// </summary>
    /// <param name="id">模型 Id</param>
    [HttpPut("models/default/{id}")]
    [ActionLogFilter("设置默认AI模型")]
    public Task<bool> SetDefaultModel([FromRoute] string id)
    {
        return _aiProviderService.SetDefaultModelAsync(id);
    }

    /// <summary>
    /// 删除模型
    /// </summary>
    /// <param name="ids">模型 Id 列表（逗号拼接）</param>
    [HttpDelete("models/deletes")]
    [ActionLogFilter("删除AI模型")]
    public Task<bool> DeletesModels([FromQuery] string ids)
    {
        return _aiProviderService.DeleteModelsAsync(SplitIds(ids));
    }

    /// <summary>
    /// 全局 AI 设置（单行）
    /// </summary>
    [HttpGet("setting")]
    public Task<AiSettingModel> GetSetting()
    {
        return _aiProviderService.GetSettingAsync();
    }

    /// <summary>
    /// 保存全局 AI 设置
    /// </summary>
    [HttpPut("setting")]
    [ActionLogFilter("保存AI设置")]
    public Task<AiSettingModel> SaveSetting([FromBody] AiSettingModel model)
    {
        return _aiProviderService.SaveSettingAsync(model);
    }

    private static List<string> SplitIds(string ids)
    {
        return (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
