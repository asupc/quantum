using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Quantum.Application;
using Quantum.Entities.Model;
using Quantum.Web.Filters;
using Quantum.Utils;

namespace Quantum.Web.Controllers;

/// <summary>
/// App 端上传/下载接口：图片与聊天附件上传（白名单+大小+限流）、鉴权流式下载。
/// 下载带 ETag/Last-Modified 支持缓存协商；图片 inline、其余 attachment + nosniff。
/// </summary>
/// <remarks>
/// 类级 ManagerOnly 与契约 §5.1 的权限清单一致：Open AppKey 匿名令牌同密钥签发但无 Manager claim，
/// 缺这道闸即可上传/下载任意附件（2026-09-18 补齐）。
/// </remarks>
[Route("api/AppUpload")]
[CustomAuthorizationFilter]
[ManagerOnly]
public class AppUploadController : BaseController
{
    private readonly AppUploadService _uploadService;

    public AppUploadController(AppUploadService uploadService)
    {
        _uploadService = uploadService;
    }

    /// <summary>
    /// 上传聊天图片（multipart 字段名 file；.jpg/.jpeg/.png/.gif/.webp，≤10MB；20 次/分钟）
    /// </summary>
    [HttpPost("image")]
    [RequestSizeLimit(AppUploadService.MaxImageBytes + 64 * 1024)]
    public async Task<AppUploadResult> Image()
    {
        var form = await Request.ReadFormAsync();
        return await _uploadService.SaveImageAsync(form.Files);
    }

    /// <summary>
    /// 上传聊天附件（multipart 字段名 file；导入白名单 + .pdf，≤50MB；20 次/分钟）
    /// </summary>
    [HttpPost("file")]
    [RequestSizeLimit(AppUploadService.MaxFileBytes + 64 * 1024)]
    public async Task<AppUploadResult> File()
    {
        var form = await Request.ReadFormAsync();
        return await _uploadService.SaveFileAsync(form.Files);
    }

    /// <summary>
    /// 鉴权下载：图片 inline 且带 ETag/Last-Modified；
    /// Content-Type 服务端按扩展名固定映射 + nosniff；非图片 Content-Disposition: attachment。
    /// </summary>
    [HttpGet("{fileId}")]
    public async Task<IActionResult> Download([FromRoute] string fileId)
    {
        var (file, absolutePath) = await _uploadService.GetForDownloadAsync(fileId);
        var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{file.Id}-{file.Size}\"");
        var result = PhysicalFile(absolutePath, file.ContentType, file.CreateTime, etag);
        result.EnableRangeProcessing = true;
        Response.Headers.XContentTypeOptions = "nosniff";
        if (AppUploadService.ImageExtensions.Contains(file.Ext))
        {
            Response.Headers.ContentDisposition = $"inline; filename=\"{Uri.EscapeDataString(file.FileName ?? file.Id)}\"";
        }
        else
        {
            Response.Headers.ContentDisposition = $"attachment; filename=\"{Uri.EscapeDataString(file.FileName ?? file.Id)}\"";
        }
        return result;
    }
}
