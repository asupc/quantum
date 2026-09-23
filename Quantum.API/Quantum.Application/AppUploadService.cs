using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 上传成功返回体：{FileId, FileName, Size}
/// </summary>
public class AppUploadResult
{
    public string FileId { get; set; }
    public string FileName { get; set; }
    public long Size { get; set; }
}

/// <summary>
/// App 端上传服务（与管理端 UploadService 分离）：图片/聊天附件两条白名单、
/// 大小双保险（控制器 [RequestSizeLimit] + 服务内 file.Length）、20 次/分钟限流；
/// Content-Type 由服务端按扩展名固定映射（不信任上传声明），下载要求有效管理令牌（单管理员）。
/// </summary>
public class AppUploadService
{
    /// <summary>图片白名单</summary>
    public static readonly List<string> ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    /// <summary>聊天附件白名单 = 导入白名单 + pdf</summary>
    public static readonly List<string> FileExtensions = [.. UploadService.ImportExtensions, ".pdf"];

    public const long MaxImageBytes = 10 * 1024 * 1024;
    public const long MaxFileBytes = 50 * 1024 * 1024;

    /// <summary>存储子目录（单管理员全局一个）</summary>
    private const string StorageFolder = "app";

    private readonly IQuantumDbContext _dbContext;

    public AppUploadService(IQuantumDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 上传聊天图片（白名单 .jpg/.jpeg/.png/.gif/.webp，≤10MB）
    /// </summary>
    public Task<AppUploadResult> SaveImageAsync(IFormFileCollection files)
    {
        return SaveAsync(files, ImageExtensions, MaxImageBytes, "图片");
    }

    /// <summary>
    /// 上传聊天附件（导入白名单 + .pdf，≤50MB）
    /// </summary>
    public Task<AppUploadResult> SaveFileAsync(IFormFileCollection files)
    {
        return SaveAsync(files, FileExtensions, MaxFileBytes, "文件");
    }

    private async Task<AppUploadResult> SaveAsync(IFormFileCollection files, List<string> allowExtends, long maxBytes, string label)
    {
        if (!MemoryObjectCache.TryAcquireAppUploadSlot(StorageFolder))
        {
            throw new BusinessException("上传过于频繁，请稍后重试！");
        }
        if (files == null || files.Count == 0)
        {
            throw new BusinessException("请选择要上传的文件！");
        }
        var file = files[0];
        // 只取纯文件名部分（部分客户端会上传带相对路径的 FileName）
        var fileName = Path.GetFileName(file.FileName);
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant() ?? "";
        if (!SafeFile.IsExtensionAllowed(fileName, allowExtends))
        {
            throw new BusinessException($"不支持的{label}类型！");
        }
        if (file.Length > maxBytes)
        {
            throw new BusinessException($"{label}大小超出限制（≤{maxBytes / 1024 / 1024}MB）！");
        }

        var fileId = Guid.NewGuid().ToString().Replace("-", "");
        var relative = $"{StorageFolder}/{fileId}{ext}";
        var filePath = SafeFile.Resolve("./db/appfiles", relative);
        if (filePath == null)
        {
            throw new BusinessException("存储路径非法！");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var model = new AppFileModel
        {
            Id = fileId,
            FileName = fileName,
            Ext = ext,
            Size = file.Length,
            ContentType = ResolveContentType(ext),
            Path = $"./db/appfiles/{relative}",
            CreateTime = DateTime.Now
        };
        _dbContext.AppFiles.Add(model);
        await _dbContext.SaveChangesAsync();

        return new AppUploadResult
        {
            FileId = model.Id,
            FileName = model.FileName,
            Size = model.Size
        };
    }

    /// <summary>
    /// 取可下载文件（物理存在校验），供控制器流式输出。
    /// </summary>
    public async Task<(AppFileModel File, string AbsolutePath)> GetForDownloadAsync(string fileId)
    {
        if (string.IsNullOrEmpty(fileId))
        {
            throw new BusinessException("文件不存在或无权访问！");
        }
        var file = await _dbContext.AppFiles.AsNoTracking().SingleOrDefaultAsync(n => n.Id == fileId);
        if (file == null)
        {
            throw new BusinessException("文件不存在或无权访问！");
        }
        var relative = file.Path.Replace("./db/appfiles/", "").Replace('\\', '/');
        var absolute = SafeFile.Resolve("./db/appfiles", relative);
        if (absolute == null || !System.IO.File.Exists(absolute))
        {
            throw new BusinessException("文件不存在或无权访问！");
        }
        return (file, absolute);
    }

    /// <summary>
    /// 按扩展名固定映射 Content-Type（nosniff 防线：服务端说了算，不信任上传声明）。
    /// </summary>
    public static string ResolveContentType(string ext)
    {
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".json" => "application/json",
            ".csv" or ".txt" => "text/plain",
            ".xml" => "application/xml",
            ".yml" => "application/x-yaml",
            ".zip" => "application/zip",
            ".db" or ".sqlite" or ".db3" => "application/octet-stream",
            ".js" => "text/javascript",
            ".py" => "text/x-python",
            ".sh" or ".bat" => "text/x-shellscript",
            _ => "application/octet-stream"
        };
    }
}
