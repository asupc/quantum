using System.Text;
using Docker.DotNet;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Plugins;

using Quantum.Utils;
namespace Quantum.Application;

/// <summary>
/// 任务执行运行时桥（计划 2.2）：Web 启动时注入根 IServiceProvider，
/// 供静态执行链路（TaskExcuteService）为每次执行创建独立 DI scope——
/// ctx.Env/ctx.Notify 门面从该 scope 解析 scoped 的 EnvService/NotifyService
/// （DbContext 隔离，事务语义与 HTTP 请求一致），执行结束 scope 释放。
/// </summary>
public static class TaskPluginHost
{
    private static IServiceProvider _serviceProvider;

    /// <summary>Startup.Configure 中调用一次。</summary>
    public static void Configure(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public static IServiceScope CreateScope()
    {
        var provider = _serviceProvider ?? throw new InvalidOperationException("TaskPluginHost 尚未初始化（应在 Web 启动时 Configure）");
        return provider.CreateScope();
    }
}

/// <summary>
/// 环境变量门面：进程内直调 EnvService（决议 6——平台内部数据不走 HTTP，免令牌/免 JSON 信封）。
/// 与 HTTP 入口的事务语义一致：同一 EnvService scoped 实例贯穿一次执行。
/// </summary>
internal sealed class QuantumEnvFacade : IQuantumEnv
{
    private readonly EnvService _envService;

    public QuantumEnvFacade(EnvService envService)
    {
        _envService = envService;
    }

    public Task<IReadOnlyList<QuantumEnvValue>> QueryAsync(string name = null, string key = null, CancellationToken ct = default)
    {
        var list = _envService.Query(name, key)
            .Select(n => new QuantumEnvValue(n.Name, n.Value, n.Remark, n.Enable, n.UpdateTime ?? n.CreateTime))
            .ToList();
        return Task.FromResult<IReadOnlyList<QuantumEnvValue>>(list);
    }

    public async Task SaveAsync(string name, string value, string remark = null, bool enabled = true, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // EnvService 按 Id 合并：先按名查库取既有 Id（缓存刚失效也准确），实现「存在即更新、不存在即新增」
        var exists = await _envService.GetByNameAsync(name);
        var model = new EnvModelPostModel
        {
            Name = name,
            Value = value,
            Remark = remark,
            Enable = enabled
        };
        if (exists != null)
        {
            model.Id = exists.Id;
        }
        await _envService.Save(new List<EnvModelPostModel> { model });
    }

    public async Task DeleteByNameAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ids = _envService.Query(name, null).Select(n => n.Id).ToList();
        if (ids.Count > 0)
        {
            await _envService.Delete(ids);
        }
    }

    public async Task SetEnabledAsync(string name, bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ids = string.Join(',', _envService.Query(name, null).Select(n => n.Id));
        if (ids.Length > 0)
        {
            if (enabled)
            {
                await _envService.EnableAsync(ids);
            }
            else
            {
                await _envService.DisableAsync(ids);
            }
        }
    }
}

/// <summary>
/// 通知门面：进程内直调 NotifyService（落 App 会话管道）。
/// 是否推送由任务自判 ctx.EnablePush（与原 system_enable_notify 环境变量语义等价）。
/// 每次任务执行独立构造，持有双键：taskId（产生消息的真实任务 Id，选项载荷根部注入供点选精确路由）
/// 与 sessionKey（出站会话键 = 任务配置的会话名 Trim ‖ 任务 Id）——该任务经 ctx.Notify 发出的消息
/// 全部落入 sessionKey 会话（多个任务配同一会话名即合并为一个客户端会话，2026-09-18 会话分组归并批次）。
/// </summary>
internal sealed class QuantumNotifyFacade : IQuantumNotify
{
    /// <summary>单条消息可携带的选项数上限（契约约束，防超大帧/列表刷屏）。</summary>
    private const int MaxOptions = 20;

    private readonly NotifyService _notifyService;
    private readonly string _taskId;
    private readonly string _sessionKey;

    public QuantumNotifyFacade(NotifyService notifyService, string taskId, string sessionKey)
    {
        _notifyService = notifyService;
        _taskId = taskId;
        _sessionKey = sessionKey;
    }

    public Task SendAsync(string title, string content, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _notifyService.Send(new SendNotifyDTO
        {
            Title = title,
            message = content,
            SessionKey = _sessionKey
        });
        return Task.CompletedTask;
    }

    public Task SendImageAsync(string imageUrl, string caption = null, IReadOnlyList<QuantumOption> options = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // 空地址由 NotifyService 入口同一份校验拒绝（message 即图片地址）
        _notifyService.Send(new SendNotifyDTO
        {
            message = imageUrl,
            message_text = caption,
            MessageType = MessageType.图片,
            Payload = BuildOptionsPayload(options),
            SessionKey = _sessionKey
        });
        return Task.CompletedTask;
    }

    public Task SendVideoAsync(string videoUrl, string caption = null, string posterUrl = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _notifyService.Send(new SendNotifyDTO
        {
            message = videoUrl,
            message_text = caption,
            MessageType = MessageType.视频,
            // 视频消息走独立的封面载荷（无选项，不注入 taskId——点选路由仅对选项消息有意义）
            Payload = BuildPosterPayload(posterUrl),
            SessionKey = _sessionKey
        });
        return Task.CompletedTask;
    }

    public Task SendAudioAsync(string audioUrl, string caption = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _notifyService.Send(new SendNotifyDTO
        {
            message = audioUrl,
            message_text = caption,
            MessageType = MessageType.音频,
            SessionKey = _sessionKey
        });
        return Task.CompletedTask;
    }

    public Task SendOptionsAsync(string content, IReadOnlyList<QuantumOption> options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _notifyService.Send(new SendNotifyDTO
        {
            message = content,
            Payload = BuildOptionsPayload(options, required: true),
            SessionKey = _sessionKey
        });
        return Task.CompletedTask;
    }

    /// <summary>组装视频封面载荷：{"poster":"地址"}；地址为空返回 null（普通视频消息）。</summary>
    private static string BuildPosterPayload(string posterUrl)
    {
        if (string.IsNullOrWhiteSpace(posterUrl))
        {
            return null;
        }
        return new Newtonsoft.Json.Linq.JObject
        {
            ["poster"] = posterUrl.Trim()
        }.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>
    /// 组装可点选项载荷：{"taskId":"<产生消息的任务Id>","options":[{key,label,reply,color,desc}]}
    /// （camelCase 与客户端 kotlinx 契约一致）。根部 taskId 用真实任务 Id（非会话键），
    /// 会话名改名不影响点选路由；服务端自动注入，脚本无感。仅 SendOptionsAsync / SendImageAsync 使用
    /// （SendVideoAsync 走 BuildPosterPayload，视频消息无选项）。
    /// reply 缺省归一为 key（客户端点选直发，不再兜底推断）；required=true 时选项空/全空视为脚本用法错误。
    /// </summary>
    private string BuildOptionsPayload(IReadOnlyList<QuantumOption> options, bool required = false)
    {
        if (options == null || options.Count == 0)
        {
            if (required)
            {
                throw new BusinessException("SendOptionsAsync 至少需要一个选项。");
            }
            return null;
        }
        if (options.Count > MaxOptions)
        {
            throw new BusinessException($"选项数量超过上限 {MaxOptions} 条。");
        }
        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option?.Key) || string.IsNullOrWhiteSpace(option.Label))
            {
                throw new BusinessException("选项的 Key 与 Label 均不能为空。");
            }
        }
        var array = new Newtonsoft.Json.Linq.JArray(options.Select(option => new Newtonsoft.Json.Linq.JObject
        {
            ["key"] = option.Key.Trim(),
            ["label"] = option.Label.Trim(),
            ["reply"] = string.IsNullOrWhiteSpace(option.Reply) ? option.Key.Trim() : option.Reply.Trim(),
            ["color"] = string.IsNullOrWhiteSpace(option.Color) ? null : option.Color.Trim(),
            ["desc"] = string.IsNullOrWhiteSpace(option.Desc) ? null : option.Desc.Trim()
        }));
        return new Newtonsoft.Json.Linq.JObject
        {
            ["taskId"] = _taskId,
            ["options"] = array
        }.ToString(Newtonsoft.Json.Formatting.None);
    }
}

/// <summary>
/// 受控文件落盘门面：任务脚本产物文件的唯一写盘通道（System.IO 在脚本门禁中仍被禁用）。
/// 可写范围限定在下载根目录（Setting.FileDownloadRoot，缺省 ./downloads）内：
/// 子目录只接受根内相对路径并拒绝穿越，文件名清洗非法字符并限长，重名自动追加序号不覆盖。
/// 复用执行器的任务 HttpClient（预配代理/超时），流式落盘，失败/取消清理残片。
/// </summary>
internal sealed class QuantumFileFacade : IQuantumFile
{
    private const int MaxFileNameLength = 120;

    /// <summary>SaveTextAsync 的文本上限（1 MiB）：挡误用（把日志/大文件当产物写盘）撑爆下载目录。</summary>
    private const int MaxTextContentLength = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _rootFullPath;

    public QuantumFileFacade(string configuredRoot, HttpClient http)
    {
        _http = http;
        _rootFullPath = SafeMediaPath.ResolveRoot(configuredRoot);
    }

    public async Task<QuantumFileResult> DownloadAsync(string url, string fileName, string subDir = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessException("文件下载地址必须以 http/https 开头！");
        }

        var targetDir = SafeMediaPath.ResolveDirectory(_rootFullPath, subDir);
        var finalPath = ResolveAvailablePath(Path.Combine(targetDir, SanitizeFileName(fileName)));
        Directory.CreateDirectory(targetDir);

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new BusinessException($"文件下载失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            var target = File.Create(finalPath);
            try
            {
                await using (target)
                {
                    await source.CopyToAsync(target, ct);
                }
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }
        catch
        {
            // 网络失败/取消/清洗后仍异常：不留半截残片（删除失败不掩盖原始异常）
            try { File.Delete(finalPath); } catch { /* 忽略 */ }
            throw;
        }

        var length = new FileInfo(finalPath).Length;
        // 落盘成功即失效 AppMedia 列表扫描缓存：脚本下载完推气泡 → 用户立刻开媒体库即可见（无 30s 盲区）
        AppMediaService.InvalidateScanCache();
        return new QuantumFileResult(finalPath, Path.GetFileName(finalPath), length,
            Path.GetRelativePath(_rootFullPath, finalPath).Replace('\\', '/'));
    }

    /// <summary>
    /// 文本覆盖落盘：目录约束与文件名清洗与 DownloadAsync 同源，但同名直接覆盖
    /// （证书这类需原地更新的产物，追加「 (n)」序号会让读侧永远拿不到新文件）。
    /// 先写同目录临时文件再原子替换，避免 nginx 这类读侧看到半截证书。
    /// </summary>
    public async Task<QuantumFileResult> SaveTextAsync(string content, string fileName, string subDir = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (content == null)
        {
            throw new BusinessException("落盘内容不能为 null！");
        }
        if (content.Length > MaxTextContentLength)
        {
            throw new BusinessException($"落盘内容超出上限 {MaxTextContentLength / 1024} KB！");
        }
        var targetDir = SafeMediaPath.ResolveDirectory(_rootFullPath, subDir);
        Directory.CreateDirectory(targetDir);
        var finalPath = Path.Combine(targetDir, SanitizeFileName(fileName));
        var tempPath = finalPath + ".tmp";
        try
        {
            // 无 BOM：PEM 前置 BOM 会让 openssl/nginx 解析失败
            await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(false), ct);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* 残片清理失败不掩盖原始异常 */ }
            throw;
        }

        AppMediaService.InvalidateScanCache();
        var realLength = new FileInfo(finalPath).Length;
        return new QuantumFileResult(finalPath, Path.GetFileName(finalPath), realLength,
            Path.GetRelativePath(_rootFullPath, finalPath).Replace('\\', '/'));
    }

    /// <summary>清洗文件名：非法字符/控制字符替换为 _，去结尾点与空格，空名兜底 download，超长截断保留扩展名。</summary>
    private static string SanitizeFileName(string fileName)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var builder = new StringBuilder();
        foreach (var ch in (fileName ?? string.Empty).Trim())
        {
            builder.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        }
        var name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "download";
        }
        if (name.Length > MaxFileNameLength)
        {
            var ext = Path.GetExtension(name);
            var keepStem = MaxFileNameLength - ext.Length;
            name = keepStem > 0 ? name[..keepStem] + ext : name[..MaxFileNameLength];
        }
        return name;
    }

    /// <summary>重名追加「 (n)」序号（n 至多 99），不覆盖既有文件。</summary>
    private static string ResolveAvailablePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }
        var dir = Path.GetDirectoryName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 100; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new BusinessException("同名文件过多，请清理下载目录后重试。");
    }
}

/// <summary>
/// Docker 运维门面：脚本可用的容器操作收敛为「重启 + 探活」（见 IQuantumDocker 注释为何不放开更多能力）。
/// DockerManagementService 经工厂延迟解析——多数任务不碰 docker，不该每次执行都新建一个 DockerClient；
/// 守护进程的原始异常在此转成含容器名的中文 BusinessException，任务日志可直接定位。
/// </summary>
internal sealed class QuantumDockerFacade : IQuantumDocker
{
    private readonly Func<DockerManagementService> _docker;

    public QuantumDockerFacade(Func<DockerManagementService> docker)
    {
        _docker = docker;
    }

    public async Task RestartAsync(string container, uint waitBeforeKillSeconds = 10, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var name = RequireContainer(container);
        try
        {
            await _docker().RestartContainerAsync(name, waitBeforeKillSeconds);
        }
        catch (DockerApiException e)
        {
            throw new BusinessException($"重启容器「{name}」失败（HTTP {(int)e.StatusCode}）：{e.Message}");
        }
    }

    public async Task<bool> IsRunningAsync(string container, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var name = RequireContainer(container);
        try
        {
            var info = await _docker().InspectContainerAsync(name);
            return info?.State?.Running == true;
        }
        catch (DockerApiException)
        {
            // 容器不存在/守护进程不可达统一按「未运行」返回：探活用于重启后复核，不该自身抛
            return false;
        }
    }

    private static string RequireContainer(string container)
    {
        var name = (container ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new BusinessException("未指定容器名！重启/探活均需传入容器名（建议取自任务环境变量）。");
        }
        return name;
    }
}

/// <summary>
/// 自定义数据门面：进程内直调 CustomDataService / CustomDataTitleService
/// （与数据管理页同一查询语义：Data1-Data15 包含匹配 + 创建时间范围）。
/// </summary>
internal sealed class QuantumCustomDataFacade : IQuantumCustomData
{
    private readonly CustomDataService _customDataService;
    private readonly CustomDataTitleService _customDataTitleService;

    public QuantumCustomDataFacade(CustomDataService customDataService, CustomDataTitleService customDataTitleService)
    {
        _customDataService = customDataService;
        _customDataTitleService = customDataTitleService;
    }

    public Task<IReadOnlyList<QuantumCustomDataValue>> QueryAsync(string type, string data1 = null,
        string data2 = null, string data3 = null, string data4 = null, string data5 = null, CancellationToken ct = default)
    {
        return QueryAsync(type, new QuantumCustomDataFilter
        {
            Data1 = data1,
            Data2 = data2,
            Data3 = data3,
            Data4 = data4,
            Data5 = data5
        }, ct);
    }

    public async Task<IReadOnlyList<QuantumCustomDataValue>> QueryAsync(string type, QuantumCustomDataFilter filter,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(type))
        {
            throw new BusinessException("查询自定义数据未指定 type！");
        }
        filter ??= new QuantumCustomDataFilter();
        // §2-8：列表端点默认收敛到 50，门面查询显式请求上限 1000（原语义为「尽量取全」）；超量数据走导出端点
        var page = await _customDataService.Get(type, ToModel(filter), filter.CreateTimeStart, filter.CreateTimeEnd, null, 1000);
        return page.Data.Select(ToValue).ToList();
    }

    public async Task AddAsync(IReadOnlyList<QuantumCustomDataValue> items, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (items == null || items.Count == 0)
        {
            return;
        }
        await _customDataService.AddAsync(items.Select(n => ToModel(n)).ToList());
    }

    public async Task UpdateAsync(IReadOnlyList<QuantumCustomDataValue> items, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (items == null || items.Count == 0)
        {
            return;
        }
        // UpdateRange 以主键定位：缺 Id 会插入重复行而非报错，挡在门面入口
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Id))
            {
                throw new BusinessException("更新自定义数据未指定 Id！请先查询取得条目再修改。");
            }
        }
        await _customDataService.UpdatesAsync(items.Select(n => ToModel(n)).ToList());
    }

    public async Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ids == null || ids.Count == 0)
        {
            return;
        }
        await _customDataService.DeleteAsync(ids.ToList());
    }

    public async Task SaveTitleAsync(string type, string typeName, string[] titles, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(type))
        {
            throw new BusinessException("保存自定义数据表头未指定 type！");
        }
        var values = titles ?? [];
        string At(int index) => index < values.Length ? values[index] : null;
        await _customDataTitleService.AddOrUpdate(new CustomDataTitleModel
        {
            Type = type,
            TypeName = typeName,
            Title1 = At(0),
            Title2 = At(1),
            Title3 = At(2),
            Title4 = At(3),
            Title5 = At(4),
            Title6 = At(5),
            Title7 = At(6),
            Title8 = At(7),
            Title9 = At(8),
            Title10 = At(9),
            Title11 = At(10),
            Title12 = At(11),
            Title13 = At(12),
            Title14 = At(13),
            Title15 = At(14)
        });
    }

    private static CustomDataModel ToModel(QuantumCustomDataValue value)
    {
        return new CustomDataModel
        {
            Id = value.Id,
            Type = value.Type,
            Data1 = value.Data1,
            Data2 = value.Data2,
            Data3 = value.Data3,
            Data4 = value.Data4,
            Data5 = value.Data5,
            Data6 = value.Data6,
            Data7 = value.Data7,
            Data8 = value.Data8,
            Data9 = value.Data9,
            Data10 = value.Data10,
            Data11 = value.Data11,
            Data12 = value.Data12,
            Data13 = value.Data13,
            Data14 = value.Data14,
            Data15 = value.Data15
        };
    }

    private static CustomDataModel ToModel(QuantumCustomDataFilter filter)
    {
        return new CustomDataModel
        {
            Data1 = filter.Data1,
            Data2 = filter.Data2,
            Data3 = filter.Data3,
            Data4 = filter.Data4,
            Data5 = filter.Data5,
            Data6 = filter.Data6,
            Data7 = filter.Data7,
            Data8 = filter.Data8,
            Data9 = filter.Data9,
            Data10 = filter.Data10,
            Data11 = filter.Data11,
            Data12 = filter.Data12,
            Data13 = filter.Data13,
            Data14 = filter.Data14,
            Data15 = filter.Data15
        };
    }

    private static QuantumCustomDataValue ToValue(CustomDataModel model)
    {
        return new QuantumCustomDataValue
        {
            Id = model.Id,
            Type = model.Type,
            Data1 = model.Data1,
            Data2 = model.Data2,
            Data3 = model.Data3,
            Data4 = model.Data4,
            Data5 = model.Data5,
            Data6 = model.Data6,
            Data7 = model.Data7,
            Data8 = model.Data8,
            Data9 = model.Data9,
            Data10 = model.Data10,
            Data11 = model.Data11,
            Data12 = model.Data12,
            Data13 = model.Data13,
            Data14 = model.Data14,
            Data15 = model.Data15,
            CreateTime = model.CreateTime,
            UpdateTime = model.UpdateTime
        };
    }
}
