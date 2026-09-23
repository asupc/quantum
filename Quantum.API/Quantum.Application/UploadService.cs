using Microsoft.AspNetCore.Http;
using log4net;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

public class UploadService
{
    private static readonly ILog _log = LogManager.GetLogger("NETCoreRepository", typeof(UploadService));

    readonly ScriptVersionService _scriptVersionService;

    public UploadService(ScriptVersionService scriptVersionService)
    {
        _scriptVersionService = scriptVersionService;
    }
    /// <summary>
    /// 脚本上传允许的扩展名（.cs 源码任务唯一形态；.js/.py 已随执行引擎改造移除）
    /// </summary>
    static readonly List<string> AllowExtends = [".cs"];

    /// <summary>
    /// db/import 是面向数据库/数据文件导入的手动投放目录（App 端规划复用），
    /// 与脚本目录不同，扩展名限制放宽到导入类文件。
    /// </summary>
    internal static readonly List<string> ImportExtensions = [".json", ".db", ".sqlite", ".db3", ".zip", ".csv", ".txt", ".xml", ".yml", ".js", ".py", ".sh", ".bat"];

    /// <summary>
    /// 上传文件到 db/import 目录
    /// </summary>
    public async Task<object> UploadToImport(IFormFileCollection files)
    {
        if (files.Count == 0)
        {
            throw new BusinessException("请选择要上传的文件！");
        }
        var file = files[0];
        // 只取纯文件名部分（部分客户端会上传带相对路径的 FileName），并限制扩展名
        var fileName = Path.GetFileName(file.FileName);
        if (!SafeFile.IsExtensionAllowed(fileName, ImportExtensions))
        {
            throw new BusinessException("不支持的文件类型！");
        }
        var filePath = SafeFile.Resolve("./db/import", fileName);
        if (filePath == null)
        {
            throw new BusinessException("文件名非法！");
        }

        if (System.IO.File.Exists(filePath))
        {
            System.IO.File.Delete(filePath);
        }

        await using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }
        return new
        {
            Path = "./db/import/" + fileName,
            FileName = fileName
        };
    }

    /// <summary>
    /// 上传脚本文件（保存流水线：门禁+编译任一不过即拒绝写入磁盘）
    /// </summary>
    public async Task<object> UploadScripts(IFormFileCollection files, string dir)
    {
        if (files.Count == 0)
        {
            throw new BusinessException("请选择要上传的文件！");
        }
        var file = files[0];
        var fileName = Path.GetFileName(file.FileName);
        if (!SafeFile.IsExtensionAllowed(fileName, AllowExtends))
        {
            throw new BusinessException($"不支持的文件类型：仅允许 .cs 源码任务（.js/.py 已停止支持）");
        }
        // dir 允许多级子目录（前端传 FileNameDir/ScriptSubDir），是否越出 scripts 根目录由 Resolve 越狱校验兜底
        var relative = string.IsNullOrEmpty(dir) ? fileName : $"{dir.TrimStart('/', '\\')}/{fileName}";
        var filePath = SafeFile.Resolve("./scripts", relative);
        if (filePath == null)
        {
            throw new BusinessException("上传路径非法！");
        }

        string content;
        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            content = await reader.ReadToEndAsync();
        }

        // 保存流水线（与在线编辑保存同一条链路）：门禁拦截/编译失败即拒绝入库
        var build = ScriptBuildService.Build(content, filePath);
        if (!build.Success)
        {
            var detail = string.Join("\n", build.Blocked.Concat(build.Errors)
                .Take(5)
                .Select(i => $"[第{i.Line}行] {i.Message}"));
            throw new BusinessException($"任务脚本未通过保存检查，已拒绝上传：\n{detail}");
        }

        var basePath = Path.GetDirectoryName(filePath)!;
        if (!Directory.Exists(basePath))
        {
            Directory.CreateDirectory(basePath);
        }

        if (System.IO.File.Exists(filePath))
        {
            System.IO.File.Delete(filePath);
        }

        await using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }
        // 版本留痕（旁路：写库异常不影响上传结果）：仅 scripts/quantum 下的任务脚本参与版本管理，
        // db/import 等其它目录的文件（ToQuantumRelative 返回 null）不记
        var versionFileName = ScriptVersionService.ToQuantumRelative(relative);
        if (versionFileName != null)
        {
            await _scriptVersionService.RecordQuietlyAsync(versionFileName, content, ScriptVersionSource.Upload);
        }
        return new
        {
            Path = "./scripts/" + relative.Replace('\\', '/'),
            FileName = fileName
        };
    }
}
