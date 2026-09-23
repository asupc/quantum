using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// Agent 工具集（2026-09-20 新增，AI 脚本修复 Agent 计划阶段三/四）：
/// 只读基线 11 个工具；写工具按 t_ai_setting 四开关动态装配（默认全关，见 <see cref="AgentWriteTools"/>）——
/// 全关时工具集与扩展前完全一致。脚本内容改动仍只能经 propose_fix 产出提案再由用户点应用（写工具不含改脚本内容）。
/// 所有文件类工具的参数都经 SafeFile 校验并限定在 scripts/quantum 根内，结果统一截断后再入上下文。
/// </summary>
public class AgentToolbox
{
    /// <summary>单条工具结果入上下文前的字符上限（防一次读取把预算吃光）</summary>
    internal const int ResultCharLimit = 8000;

    /// <summary>read_script 单次最多返回行数</summary>
    internal const int MaxReadLines = 800;

    /// <summary>脚本树不展示的目录（影子试运行产物等）</summary>
    internal static readonly string[] ExcludedDirs = ["agent-tmp", "node_modules", ".git"];

    private readonly IQuantumDbContext _db;
    private readonly bool _allowEnvValues;
    private readonly string _conversationId;
    private readonly string _testRunNotifyMode;
    private readonly AgentProposalService _proposalService;
    private readonly AgentTestRunService _testRunService;
    private readonly AgentWriteTools _writeTools;
    private readonly AiAgentWritePermissions _writePermissions;

    public AgentToolbox(IQuantumDbContext db, bool allowEnvValues, string conversationId,
        AgentProposalService proposalService, AgentTestRunService testRunService, string testRunNotifyMode,
        AgentWriteTools writeTools = null, AiAgentWritePermissions writePermissions = null)
    {
        _db = db;
        _allowEnvValues = allowEnvValues;
        _conversationId = conversationId;
        _proposalService = proposalService;
        _testRunService = testRunService;
        _testRunNotifyMode = testRunNotifyMode ?? AiTestRunNotifyMode.Task;
        _writeTools = writeTools;
        _writePermissions = writePermissions;
        // 实际下发给 LLM 的工具集 = 只读基线 + 按开关拼入的写工具（全关时与只读基线完全一致）
        var tools = new List<LlmTool>(ReadOnlyTools);
        tools.AddRange(AgentWriteTools.BuildTools(writePermissions));
        Tools = tools;
    }

    /// <summary>本次运行实际可用的工具集（实例属性：写工具按权限快照动态拼入）。</summary>
    public IReadOnlyList<LlmTool> Tools { get; }

    /// <summary>
    /// 只读基线工具（11 个，function calling schema）。与 <see cref="InvokeAsync"/> 的白名单一一对应；
    /// 写工具不在其中——由 <see cref="AgentWriteTools"/> 按开关拼装进 <see cref="Tools"/>。
    /// </summary>
    public static IReadOnlyList<LlmTool> ReadOnlyTools { get; } =
    [
        new LlmTool
        {
            Name = "list_scripts",
            Description = "列出全部任务脚本：相对路径、大小、行数、被哪个任务引用、最近一次执行是否失败。排查前先用它确认文件名。",
            ParametersJson = """{"type":"object","properties":{}}"""
        },
        new LlmTool
        {
            Name = "read_script",
            Description = "按行区间读取脚本源码（默认前 400 行，单次最多 800 行）。脚本很长时先读开头，再按需读局部。",
            ParametersJson = """{"type":"object","properties":{"file":{"type":"string","description":"脚本相对路径，如 B站任务.cs"},"startLine":{"type":"integer","description":"起始行（1 起，可省略）"},"endLine":{"type":"integer","description":"结束行（含，可省略）"}},"required":["file"]}"""
        },
        new LlmTool
        {
            Name = "search_scripts",
            Description = "在所有脚本里按正则搜索，返回「文件:行号: 文本」，最多 100 条。用于查找某个方法/变量/常量的用法。",
            ParametersJson = """{"type":"object","properties":{"pattern":{"type":"string","description":"正则表达式（大小写敏感）"}},"required":["pattern"]}"""
        },
        new LlmTool
        {
            Name = "get_task_info",
            Description = "按脚本文件名或任务名查询任务配置：触发指令、Cron、会话名、是否推送、环境变量来源、最近执行结果。",
            ParametersJson = """{"type":"object","properties":{"fileOrTaskName":{"type":"string","description":"脚本相对路径或任务名称"}},"required":["fileOrTaskName"]}"""
        },
        new LlmTool
        {
            Name = "read_run_log",
            Description = "读取该脚本最近一次执行的日志尾部（含 ctx.Log 输出与异常堆栈）。配合 lines 参数控制行数。",
            ParametersJson = """{"type":"object","properties":{"file":{"type":"string","description":"脚本相对路径"},"lines":{"type":"integer","description":"读取的尾部行数，默认 200，最多 2000"},"logId":{"type":"string","description":"指定日志记录 Id（不填则取该脚本最近一次）"}},"required":["file"]}"""
        },
        new LlmTool
        {
            Name = "list_envs",
            Description = "列出平台环境变量：名称、备注、启用状态、更新时间（值默认隐藏——凭据类内容不发给模型；仅当会话显式允许时才含明文值）。",
            ParametersJson = """{"type":"object","properties":{"keyword":{"type":"string","description":"按名称过滤（包含匹配，可省略）"}},"required":[]}"""
        },
        new LlmTool
        {
            Name = "query_custom_data",
            Description = "只读查询某个自定义数据类型的最新若干行（默认 20，最多 50），含该类型的列标题定义。用于判断状态机/去重记录。",
            ParametersJson = """{"type":"object","properties":{"type":{"type":"string","description":"自定义数据类型（如 haojia_seen）"},"key":{"type":"string","description":"对 Data1 做包含匹配的关键字（可省略）"},"limit":{"type":"integer","description":"返回行数，默认 20，最多 50"}},"required":["type"]}"""
        },
        new LlmTool
        {
            Name = "list_capabilities",
            Description = "重新获取平台能力摘要（脚本可用门面方法、门禁规则、可引用程序集、编写约定）。",
            ParametersJson = """{"type":"object","properties":{}}"""
        },
        new LlmTool
        {
            Name = "compile_script",
            Description = "用平台的保存流水线校验一份候选源码（安全门禁 + Roslyn 编译），返回 blocked/errors/warnings 三类诊断，不落盘。改完代码先调它自检。",
            ParametersJson = """{"type":"object","properties":{"file":{"type":"string","description":"脚本相对路径（用于行号映射与契约检查）"},"source":{"type":"string","description":"候选源码全文"}},"required":["file","source"]}"""
        },
        new LlmTool
        {
            Name = "test_run",
            Description = "影子试运行一份候选源码：真实执行（真实环境变量、真实通知门面，会产生真实副作用），返回执行结果与日志尾部。编译通过后就该跑它验证。",
            ParametersJson = """{"type":"object","properties":{"file":{"type":"string","description":"脚本相对路径"},"source":{"type":"string","description":"候选源码全文"},"silentNotify":{"type":"boolean","description":"本次试运行是否静默（不发通知消息），默认 false"}},"required":["file","source"]}"""
        },
        new LlmTool
        {
            Name = "propose_fix",
            Description = "生成待确认的修复提案（不落盘）：用户会在提案卡上看到改动说明与差异，并选择试运行/应用/忽略。源码必须先通过编译校验。",
            ParametersJson = """{"type":"object","properties":{"file":{"type":"string","description":"脚本相对路径"},"source":{"type":"string","description":"修改后的完整源码"},"summary":{"type":"string","description":"中文改动说明：根因 + 改了什么 + 为什么"}},"required":["file","source","summary"]}"""
        }
    ];

    /// <summary>
    /// 执行一个工具。只读工具走本类白名单；未命中时转 <see cref="AgentWriteTools"/>（写工具，执行前有开关+触发源二次校验），
    /// 仍不认识才拒绝——模型无法越权调用不存在的能力。
    /// </summary>
    public async Task<string> InvokeAsync(string name, JObject args, CancellationToken ct = default)
    {
        args ??= [];
        var known = name switch
        {
            "list_scripts" => await ListScriptsAsync(ct),
            "read_script" => await ReadScriptAsync(Text(args, "file"), Int(args, "startLine"), Int(args, "endLine")),
            "search_scripts" => await SearchScriptsAsync(Text(args, "pattern"), ct),
            "get_task_info" => await GetTaskInfoAsync(Text(args, "fileOrTaskName"), ct),
            "read_run_log" => await ReadRunLogAsync(Text(args, "file"), Int(args, "lines") ?? 200, Text(args, "logId")),
            "list_envs" => await ListEnvsAsync(Text(args, "keyword"), ct),
            "query_custom_data" => await QueryCustomDataAsync(Text(args, "type"), Text(args, "key"), Int(args, "limit") ?? 20, ct),
            "list_capabilities" => ScriptContractCatalog.Describe(),
            "compile_script" => CompileScript(Text(args, "file"), Text(args, "source")),
            "test_run" => await TestRunAsync(Text(args, "file"), Text(args, "source"), Bool(args, "silentNotify")),
            "propose_fix" => await ProposeFixAsync(Text(args, "file"), Text(args, "source"), Text(args, "summary")),
            _ => null
        };
        if (known != null)
        {
            return known;
        }
        if (_writeTools != null)
        {
            var writeResult = await _writeTools.TryInvokeAsync(name, args, _writePermissions, _conversationId, ct);
            if (writeResult != null)
            {
                return writeResult;
            }
        }
        return $"不支持的工具：{name}（可用工具：{string.Join(", ", Tools.Select(n => n.Name))}）";
    }

    // ==================================================================== 只读工具

    private async Task<string> ListScriptsAsync(CancellationToken ct)
    {
        var files = EnumerateScripts();
        if (files.Count == 0)
        {
            return "(scripts/quantum 下没有脚本)";
        }
        var tasks = await _db.Tasks.AsNoTracking().ToListAsync(ct);
        var sb = new StringBuilder();
        foreach (var file in files)
        {
            var relative = RelativePath(file);
            var info = new FileInfo(file);
            var lines = File.ReadLines(file).Count();
            var owners = tasks.Where(n => string.Equals(n.FileName, relative, StringComparison.OrdinalIgnoreCase)).ToList();
            var owner = owners.Count == 0
                ? "未被任务引用"
                : string.Join("/", owners.Select(n => $"{n.Name}{(n.Enable ? "" : "(已禁用)")}"));
            sb.AppendLine($"{relative}  {info.Length}B  {lines}行  任务：{owner}");
        }
        return Truncate(sb.ToString());
    }

    private async Task<string> ReadScriptAsync(string file, int? startLine, int? endLine)
    {
        var path = Resolve(file);
        if (path == null || !File.Exists(path))
        {
            return $"脚本不存在：{file}";
        }
        var lines = await File.ReadAllLinesAsync(path);
        var from = Math.Max(1, startLine ?? 1);
        var to = Math.Min(lines.Length, endLine ?? Math.Min(lines.Length, from + 399));
        if (from > lines.Length)
        {
            return $"起始行超出范围（文件共 {lines.Length} 行）";
        }
        to = Math.Min(to, from + MaxReadLines - 1);
        var sb = new StringBuilder();
        sb.AppendLine($"{file} 第 {from}-{to} 行（共 {lines.Length} 行）：");
        for (var i = from; i <= to; i++)
        {
            sb.AppendLine($"{i,5}| {lines[i - 1]}");
        }
        return Truncate(sb.ToString());
    }

    private async Task<string> SearchScriptsAsync(string pattern, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "pattern 不能为空";
        }
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
        }
        catch (Exception e)
        {
            return $"正则不合法：{e.Message}";
        }
        var sb = new StringBuilder();
        var hits = 0;
        foreach (var file in EnumerateScripts())
        {
            ct.ThrowIfCancellationRequested();
            var lines = await File.ReadAllLinesAsync(file, ct);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i]))
                {
                    continue;
                }
                sb.AppendLine($"{RelativePath(file)}:{i + 1}: {lines[i].Trim()}");
                if (++hits >= 100)
                {
                    sb.AppendLine("…（已达 100 条上限）");
                    return Truncate(sb.ToString());
                }
            }
        }
        return hits == 0 ? "(没有命中)" : Truncate(sb.ToString());
    }

    private async Task<string> GetTaskInfoAsync(string fileOrTaskName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileOrTaskName))
        {
            return "fileOrTaskName 不能为空";
        }
        var tasks = await _db.Tasks.AsNoTracking().ToListAsync(ct);
        var matched = tasks.Where(n =>
            string.Equals(n.FileName, fileOrTaskName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(n.Name, fileOrTaskName, StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(n.FileName), Path.GetFileName(fileOrTaskName), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matched.Count == 0)
        {
            return $"没有匹配的任务（可用 list_scripts 查看脚本与其归属任务）：{fileOrTaskName}";
        }
        var sb = new StringBuilder();
        foreach (var task in matched)
        {
            sb.AppendLine($"任务：{task.Name}（Id={task.Id}）");
            sb.AppendLine($"  脚本：{task.FileName}");
            sb.AppendLine($"  触发指令：{task.Command ?? "(空)"}  正则：{(task.EnableRegex ? "是" : "否")}");
            sb.AppendLine($"  定时：{task.Cron ?? "(无)"}  启用：{(task.Enable ? "是" : "否")}  消息推送：{(task.EnablePush ? "是" : "否")}");
            sb.AppendLine($"  会话名：{task.SessionName ?? "(未配置，落任务 Id 会话)"}  强制结束(min)：{(task.WaitTime == 0 ? 60 : task.WaitTime)}");
            sb.AppendLine($"  备注：{task.Remark ?? "(无)"}");
            sb.AppendLine($"  最近执行日志：{await DescribeLatestLogAsync(task.FileName)}");
        }
        return Truncate(sb.ToString());
    }

    private async Task<string> ReadRunLogAsync(string file, int lines, string logId)
    {
        if (!string.IsNullOrWhiteSpace(logId))
        {
            var log = await _db.Logs.AsNoTracking().FirstOrDefaultAsync(n => n.Id == logId);
            if (log == null)
            {
                return $"日志记录不存在：{logId}";
            }
            return ReadLogFile(log.DirectoryName, log.LogPath, lines);
        }
        var path = Resolve(file);
        if (path == null)
        {
            return $"脚本路径非法：{file}";
        }
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "logs", TaskExcuteService.LogDirNameFrom(file));
        if (!Directory.Exists(dir))
        {
            return $"该脚本还没有执行日志（目录 {dir} 不存在）";
        }
        var latest = Directory.GetFiles(dir, "*.log")
            .OrderByDescending(n => Path.GetFileName(n), StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest == null)
        {
            return "该脚本还没有执行日志";
        }
        var relative = Path.GetFileName(latest);
        return ReadLogFile(TaskExcuteService.LogDirNameFrom(file), relative, lines);
    }

    private async Task<string> DescribeLatestLogAsync(string fileName)
    {
        try
        {
            var dir = Path.Combine(Directory.GetCurrentDirectory(), "logs", TaskExcuteService.LogDirNameFrom(fileName));
            if (!Directory.Exists(dir))
            {
                return "(无)";
            }
            var latest = Directory.GetFiles(dir, "*.log")
                .OrderByDescending(n => Path.GetFileName(n), StringComparer.Ordinal)
                .FirstOrDefault();
            if (latest == null)
            {
                return "(无)";
            }
            var content = await File.ReadAllTextAsync(latest);
            var hasException = content.Contains("exception information", StringComparison.OrdinalIgnoreCase)
                || content.Contains("MessageAsync：", StringComparison.Ordinal);
            return $"{Path.GetFileName(latest)}（{(hasException ? "含异常" : "正常结束")}，{new FileInfo(latest).Length}B）";
        }
        catch (Exception e)
        {
            return $"(读取失败：{e.Message})";
        }
    }

    private static string ReadLogFile(string directoryName, string logPath, int lines)
    {
        var tail = Math.Clamp(lines, 1, 2000);
        var path = SafeFile.Resolve("./logs", $"{directoryName}/{logPath}");
        if (path == null || !File.Exists(path))
        {
            return $"日志文件不存在：logs/{directoryName}/{logPath}";
        }
        var all = File.ReadAllLines(path);
        var start = Math.Max(0, all.Length - tail);
        var sb = new StringBuilder();
        sb.AppendLine($"logs/{directoryName}/{logPath} 尾部 {all.Length - start}/{all.Length} 行：");
        for (var i = start; i < all.Length; i++)
        {
            sb.AppendLine(all[i]);
        }
        return Truncate(sb.ToString());
    }

    private async Task<string> ListEnvsAsync(string keyword, CancellationToken ct)
    {
        var envs = await _db.Envs.AsNoTracking().OrderBy(n => n.Name).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            envs = envs.Where(n => (n.Name ?? string.Empty).Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (envs.Count == 0)
        {
            return "(没有匹配的环境变量)";
        }
        var sb = new StringBuilder();
        sb.AppendLine(_allowEnvValues
            ? "变量（本会话已允许读取值，凭据明文，勿写入代码）："
            : "变量（值默认隐藏；若判断需要读值，请让用户在会话设置里开启「允许读取变量值」）：");
        foreach (var env in envs.Take(300))
        {
            var value = _allowEnvValues
                ? env.Value
                : $"(已隐藏，长度 {(env.Value ?? string.Empty).Length})";
            sb.AppendLine($"{env.Name} = {value}  启用：{(env.Enable ? "是" : "否")}  备注：{env.Remark ?? "-"}  更新：{env.UpdateTime:yyyy-MM-dd HH:mm}");
        }
        return Truncate(sb.ToString());
    }

    private async Task<string> QueryCustomDataAsync(string type, string key, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "type 不能为空";
        }
        var take = Math.Clamp(limit, 1, 50);
        var title = await _db.CustomDataTitles.AsNoTracking().FirstOrDefaultAsync(n => n.Type == type, ct);
        var query = _db.CustomDatas.AsNoTracking().Where(n => n.Type == type);
        if (!string.IsNullOrWhiteSpace(key))
        {
            query = query.Where(n => n.Data1 != null && n.Data1.Contains(key));
        }
        var rows = await query.OrderByDescending(n => n.CreateTime).Take(take).ToListAsync(ct);
        var total = await query.CountAsync(ct);
        var sb = new StringBuilder();
        if (title != null)
        {
            var columns = new[] { title.Title1, title.Title2, title.Title3, title.Title4, title.Title5 }
                .Where(n => !string.IsNullOrWhiteSpace(n));
            sb.AppendLine($"类型 {type}（{title.TypeName}）列定义：{string.Join(" | ", columns)}");
        }
        sb.AppendLine($"共 {total} 行，返回最新 {rows.Count} 行：");
        foreach (var row in rows)
        {
            sb.AppendLine($"[{row.CreateTime:yyyy-MM-dd HH:mm}] {(row.Data1 ?? "-")} | {(row.Data2 ?? "-")} | {(row.Data3 ?? "-")} | {(row.Data4 ?? "-")} | {(row.Data5 ?? "-")}");
        }
        return Truncate(sb.ToString());
    }

    /// <summary>
    /// 保存流水线干跑：门禁 + 契约预检 + Roslyn 编译，返回三类诊断（不落盘）。
    /// </summary>
    private static string CompileScript(string file, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "source 不能为空";
        }
        var scriptFile = Resolve(file) ?? file;
        var build = ScriptBuildService.Build(source, scriptFile);
        var sb = new StringBuilder();
        sb.AppendLine(build.Success ? "校验通过（安全门禁 + 编译）。" : "校验未通过：");
        foreach (var issue in build.Blocked)
        {
            sb.AppendLine($"[门禁 第{issue.Line}行] {issue.Message}");
        }
        foreach (var issue in build.Errors)
        {
            sb.AppendLine($"[编译 第{issue.Line}行] {issue.Message}");
        }
        foreach (var issue in build.Warnings)
        {
            sb.AppendLine($"[警告 第{issue.Line}行] {issue.Message}");
        }
        return Truncate(sb.ToString());
    }

    // ==================================================================== 试运行与提案

    /// <summary>
    /// 影子试运行（真实执行链，副作用真实发生）：落到 agent-tmp 后整链复用，跑完删影子文件。
    /// </summary>
    private async Task<string> TestRunAsync(string file, string source, bool silentNotify)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "source 不能为空";
        }
        if (Resolve(file) == null)
        {
            return $"脚本路径非法：{file}";
        }
        var mode = silentNotify ? AiTestRunNotifyMode.Silent : _testRunNotifyMode;
        var result = await _testRunService.RunAsync(file, source, mode, CancellationToken.None);
        var sb = new StringBuilder();
        sb.AppendLine(result.Message);
        sb.AppendLine($"试运行耗时：{result.DurationMs} ms；日志 Id：{result.LogId ?? "(无)"}");
        sb.AppendLine("日志尾部：");
        sb.AppendLine(result.LogTail);
        return Truncate(sb.ToString());
    }

    /// <summary>
    /// 生成提案（不落盘）：先复校门禁+编译（不过则要求模型先改），再入库并挂回提案卡消息。
    /// 返回值以 __PROPOSAL__ 标记开头，AgentService 据此收敛循环并把 run 置为「待确认」。
    /// </summary>
    private async Task<string> ProposeFixAsync(string file, string source, string summary)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(summary))
        {
            return "file/source/summary 都不能为空";
        }
        var scriptFile = Resolve(file);
        if (scriptFile == null)
        {
            return $"脚本路径非法：{file}";
        }
        if (!File.Exists(scriptFile))
        {
            return $"脚本不存在：{file}（新脚本请让用户先在脚本编辑页创建）";
        }
        var build = ScriptBuildService.Build(source, scriptFile);
        var diagnostics = System.Text.Json.JsonSerializer.Serialize(new
        {
            build.Blocked,
            build.Errors,
            build.Warnings
        });
        if (!build.Success)
        {
            return "提案被拒：候选源码未通过平台校验，请先修好再提交。\n" + CompileScript(file, source);
        }
        var proposal = await _proposalService.CreateAsync(_conversationId, null, file, source, summary, diagnostics);
        await _proposalService.AttachMessageAsync(proposal.Id, _conversationId);
        return $"__PROPOSAL__:{proposal.Id}\n提案已生成（待用户确认）：{file}\n说明：{summary}";
    }

    // ==================================================================== 辅助

    /// <summary>枚举 scripts/quantum 下的全部脚本（排除影子目录等）。</summary>
    internal static List<string> EnumerateScripts()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "quantum");
        if (!Directory.Exists(root))
        {
            return [];
        }
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(n => !ExcludedDirs.Any(dir => n.Contains($"{Path.DirectorySeparatorChar}{dir}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string RelativePath(string fullPath)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "quantum") + Path.DirectorySeparatorChar;
        return Path.GetFullPath(fullPath).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(fullPath)[root.Length..].Replace('\\', '/')
            : Path.GetFileName(fullPath);
    }

    private static string Resolve(string file)
        => string.IsNullOrWhiteSpace(file) ? null : SafeFile.Resolve("./scripts/quantum", file);

    private static string Text(JObject args, string key)
        => args[key]?.Type == JTokenType.String ? args[key].Value<string>()?.Trim() : null;

    private static int? Int(JObject args, string key)
        => args[key]?.Type is JTokenType.Integer or JTokenType.Float ? args[key].Value<int?>() : null;

    private static bool Bool(JObject args, string key)
        => args[key]?.Type == JTokenType.Boolean && args[key].Value<bool>();

    internal static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        return text.Length <= ResultCharLimit
            ? text
            : text[..ResultCharLimit] + $"\n…（已截断，原始长度 {text.Length} 字符）";
    }
}
