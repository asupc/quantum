using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 脚本版本管理（管理员专用，2026-09-20 新增）：脚本历史快照的查看、对比与回滚。
/// 当前版本始终以磁盘文件为准（读取走既有 GET /api/Task/scripts），本控制器只管历史。
/// </summary>
[CustomAuthorizationFilter]
[ManagerOnly]
public class ScriptVersionController : BaseController
{
    private readonly ScriptVersionService _scriptVersionService;

    public ScriptVersionController(ScriptVersionService scriptVersionService)
    {
        _scriptVersionService = scriptVersionService;
    }

    /// <summary>
    /// 版本列表（不含正文；fileName 为空则查全部文件）
    /// </summary>
    /// <param name="fileName">脚本文件（相对 scripts/quantum 根）</param>
    /// <param name="pageIndex">页码</param>
    /// <param name="pageSize">分页大小</param>
    [HttpGet]
    public Task<PageResult<ScriptVersionItem>> Get([FromQuery] string fileName, [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20)
    {
        return _scriptVersionService.GetPageAsync(fileName, pageIndex, pageSize);
    }

    /// <summary>
    /// 有版本记录的脚本文件清单（版本页左侧筛选）
    /// </summary>
    [HttpGet("files")]
    public Task<List<ScriptVersionFileItem>> Files()
    {
        return _scriptVersionService.GetFilesAsync();
    }

    /// <summary>
    /// 版本详情（含正文）
    /// </summary>
    /// <param name="id">版本 Id</param>
    [HttpGet("{id}")]
    public Task<ScriptVersionModel> GetById([FromRoute] string id)
    {
        return _scriptVersionService.GetAsync(id);
    }

    /// <summary>
    /// 回滚到指定版本：历史内容同样过门禁与编译，不通过即拒绝且不落盘（与在线保存同口径）
    /// </summary>
    /// <param name="id">版本 Id</param>
    [HttpPost("{id}/rollback")]
    [ActionLogFilter("回滚脚本版本")]
    public Task<ScriptBuildService.ScriptSaveResult> Rollback([FromRoute] string id)
    {
        return _scriptVersionService.RollbackAsync(id, GetUserId());
    }

    /// <summary>
    /// 锁定/解锁版本（锁定版本不被每文件保留数清理）
    /// </summary>
    /// <param name="id">版本 Id</param>
    /// <param name="pinned">是否锁定</param>
    [HttpPut("{id}/pin")]
    [ActionLogFilter("锁定脚本版本")]
    public Task<bool> SetPinned([FromRoute] string id, [FromQuery] bool pinned)
    {
        return _scriptVersionService.SetPinnedAsync(id, pinned);
    }

    /// <summary>
    /// 删除版本记录（只删历史快照，不触碰磁盘上的当前脚本文件）
    /// </summary>
    /// <param name="ids">版本 Id 列表（逗号拼接）</param>
    [HttpDelete("deletes")]
    [ActionLogFilter("删除脚本版本")]
    public Task<bool> Deletes([FromQuery] string ids)
    {
        var list = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return _scriptVersionService.DeleteAsync(list);
    }
}
