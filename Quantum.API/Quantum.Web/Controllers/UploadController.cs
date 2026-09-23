using Microsoft.AspNetCore.Mvc;
using Quantum.Application;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// Web 管理端上传（脚本/导入文件投放）：管理员专用——上传目标是服务端可执行/可导入目录，
/// 不对普通 App 用户开放（App 用户附件走 api/AppUpload，隔离存储+归属校验）。
/// </summary>
[CustomAuthorizationFilter]
[ManagerOnly]
public class UploadController : BaseController
{
    /// <summary>
    /// 单文件上传上限（100MB：脚本/导入文件远小于此，防磁盘与带宽耗尽）
    /// </summary>
    private const long MaxUploadBytes = 100 * 1024 * 1024;

    readonly UploadService uploadService;

    public UploadController(UploadService uploadService)
    {
        this.uploadService = uploadService;
    }

    [HttpPost]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<object> Index()
    {
        var form = await Request.ReadFormAsync();
        return await uploadService.UploadToImport(form.Files);
    }

    [ActionLogFilter("上传脚本文件")]
    [HttpPost("scripts")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<object> UploadScripts([FromQuery] string dir)
    {
        var form = await Request.ReadFormAsync();
        return await uploadService.UploadScripts(form.Files, dir);
    }
}
