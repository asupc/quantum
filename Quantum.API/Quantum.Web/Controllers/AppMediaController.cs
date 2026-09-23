using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Quantum.Application;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// App 媒体通道（M2）：下载根目录（ctx.File 门面产物）的列表/流式下发/内嵌封面。
/// 鉴权比 AppUpload 下载更严：[ManagerOnly] 正向 claim，Open 匿名令牌/任务临时令牌天然被拒。
/// list 走 ResultFilter 信封；file/cover 为二进制直出（Range/ETag/inline/nosniff），
/// 校验失败直出 404（不套 200 信封——ExoPlayer 对 200+JSON 只会报底层解析错误）。
/// </summary>
[Route("api/AppMedia")]
[CustomAuthorizationFilter]
[ManagerOnly]
public class AppMediaController : BaseController
{
    private readonly AppMediaService _mediaService;

    public AppMediaController(AppMediaService mediaService)
    {
        _mediaService = mediaService;
    }

    /// <summary>
    /// 媒体列表：mtime 倒序；keyword 匹配文件名（包含）；dir 限定子目录（缺省全部）。
    /// title/artist 取 ID3 元数据，解析失败回退「歌手 - 歌名」文件名拆分。
    /// </summary>
    [HttpGet("list")]
    public Task<AppMediaService.MediaListResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] string keyword = null, [FromQuery] string dir = null)
    {
        return _mediaService.ListAsync(page, pageSize, keyword, dir);
    }

    /// <summary>
    /// 媒体流式下发：支持 Range（206 拖动/连播）、ETag("mtime-size") 缓存协商、
    /// Content-Type 按扩展名固定映射；越界/白名单外/不存在直出 404。
    /// </summary>
    [HttpGet("file")]
    public IActionResult Stream([FromQuery] string path)
    {
        if (!_mediaService.TryGetFile(path, out var absolutePath, out var contentType, out var lastModified, out var size))
        {
            return NotFoundDirect();
        }
        var etag = new EntityTagHeaderValue($"\"{lastModified.Ticks}-{size}\"");
        var result = PhysicalFile(absolutePath, contentType, lastModified, etag);
        result.EnableRangeProcessing = true;
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentDisposition = "inline";
        return result;
    }

    /// <summary>内嵌封面（ID3 picture 字节，ETag 同键缓存）；无封面 404，App 回退占位图。</summary>
    [HttpGet("cover")]
    public IActionResult Cover([FromQuery] string path)
    {
        if (!_mediaService.TryGetCover(path, out var cover, out var mime))
        {
            return NotFoundDirect();
        }
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(cover, mime);
    }

    /// <summary>二进制端点的直出 404：极简 JSON、不回显请求路径（ContentResult 不经 ResultFilter 包装）。</summary>
    private static IActionResult NotFoundDirect()
    {
        return new ContentResult
        {
            StatusCode = 404,
            ContentType = "application/json",
            Content = "{\"Code\":404,\"Message\":\"文件不存在或不可播放\"}"
        };
    }
}
