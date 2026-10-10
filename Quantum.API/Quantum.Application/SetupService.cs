using System.Text;
using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.Config;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 首次部署初始化向导服务（/api/Setup/*）：未初始化状态判定、自带脚本清单、数据库试连、
/// 一次性完成初始化（创建登录账号 + 选择数据库 + 按勾选建任务）。
/// 仅 <see cref="SystemConfigHelper.NeedSetup"/> 期间开放：初始化完成后 complete/test-database
/// 一律拒绝（防未授权改密/接管账号、防被当内网探测跳板）。
/// </summary>
public class SetupService
{
    /// <summary>初始化向导设置的登录密码最短长度（改密链路不限制，此处是首建账号的基础加固）。</summary>
    public const int MinPasswordLength = 6;

    readonly IQuantumDbContext db;
    readonly string sourceDirectory;
    readonly string runtimeDirectory;

    public SetupService(IQuantumDbContext db)
        : this(db, DefaultScriptSourceDirectory, DefaultScriptRuntimeDirectory)
    {
    }

    public SetupService(IQuantumDbContext db, string sourceDirectory, string runtimeDirectory)
    {
        this.db = db;
        this.sourceDirectory = sourceDirectory;
        this.runtimeDirectory = runtimeDirectory;
    }

    /// <summary>随应用分发的自带脚本目录（发布产物 scripts/，顶层 .cs 即「自带脚本」全集）。</summary>
    public static string DefaultScriptSourceDirectory => Path.Combine(AppContext.BaseDirectory, "scripts");

    /// <summary>任务脚本运行目录（运行目录 scripts/quantum）：建任务时把脚本源码复制进来。</summary>
    public static string DefaultScriptRuntimeDirectory => Path.Combine(Directory.GetCurrentDirectory(), "scripts", "quantum");

    /// <summary>
    /// 向导状态：未初始化时给出当前库配置与自带脚本清单；
    /// 已初始化后只回 NeedSetup=false（匿名端点，不外泄库配置/脚本清单）。
    /// </summary>
    public SetupStatus GetStatus()
    {
        var setting = SystemConfigHelper.GetSetting();
        if (!SystemConfigHelper.NeedSetup(setting))
        {
            return new SetupStatus { NeedSetup = false };
        }
        return new SetupStatus
        {
            NeedSetup = true,
            DBType = setting.DBType,
            DBAddress = setting.DBAddress,
            Scripts = ListBundledScripts()
        };
    }

    /// <summary>自带脚本清单：顶层 .cs 文件 + 头部注释用途说明（向导第 3 步勾选用）。</summary>
    public List<SetupScriptItem> ListBundledScripts()
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return new List<SetupScriptItem>();
        }
        return Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(path => new SetupScriptItem
            {
                FileName = Path.GetFileName(path),
                Name = Path.GetFileNameWithoutExtension(path),
                Description = ExtractDescription(File.ReadAllText(path))
            })
            .ToList();
    }

    /// <summary>
    /// 数据库试连（向导第 2 步）：SQLite 校验文件名合法且 db/ 目录可写；MySql 实连握手验证地址/账号可达。
    /// 仅未初始化期可用，并按 IP 固定窗口限流（防被当内网探测跳板）。
    /// </summary>
    public SetupDatabaseTestResult TestDatabase(SetupDatabaseRequest request, string ip)
    {
        if (!SystemConfigHelper.NeedSetup(SystemConfigHelper.GetSetting()))
        {
            throw new BusinessException("系统已经初始化过了，无需测试数据库！");
        }
        if (!MemoryObjectCache.TryAcquireSetupProbeSlot(ip))
        {
            throw new BusinessException("请求频繁，请稍后再试！");
        }

        var dbType = NormalizeDbType(request?.DBType);
        try
        {
            if (dbType == "SQLite")
            {
                ValidateSqliteFileName(request.DBAddress, createProbe: true);
                return new SetupDatabaseTestResult
                {
                    Success = true,
                    Message = $"SQLite 校验通过：{request.DBAddress.Trim()}（落 db/ 目录，无需连接）"
                };
            }

            var address = ValidateMySqlAddress(request.DBAddress);
            var options = new DbContextOptionsBuilder<QuantumMySqlDbContext>()
                .UseMySQL(QuantumMySqlDbContext.NormalizeCharsetToUtf8mb4(address))
                .Options;
            using var probe = new QuantumMySqlDbContext(options);
            if (!probe.Database.CanConnect())
            {
                return new SetupDatabaseTestResult { Success = false, Message = "连接失败：无法建立数据库连接" };
            }
            return new SetupDatabaseTestResult { Success = true, Message = "MySQL 连接成功" };
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (Exception e)
        {
            // 取最内层原因：EF/驱动外层包装消息无信息量
            for (var inner = e; inner.InnerException != null; inner = inner.InnerException)
            {
                e = inner;
            }
            return new SetupDatabaseTestResult { Success = false, Message = $"连接失败：{e.Message}" };
        }
    }

    /// <summary>
    /// 一次性完成初始化：创建登录账号 + 保存数据库选择 + 按勾选建任务。
    /// 「检查 + 写配置」在配置写锁内原子完成（防并发双初始化）；建任务幂等（按文件名去重）。
    /// 数据库与本次启动一致 → 立即建任务；发生切换（如 SQLite→MySQL）→ 勾选清单暂存配置，
    /// 重启后由启动播种入口（Quantum.Web.ScriptTaskSeeder.RunAfterInit）在新库补种（换库必须重启才生效）。
    /// </summary>
    public SetupCompleteResult Complete(SetupCompleteRequest request)
    {
        if (request == null)
        {
            throw new BusinessException("缺少初始化内容！");
        }
        var userName = (request.UserName ?? "").Trim();
        var passWord = request.PassWord ?? "";
        if (string.IsNullOrEmpty(userName))
        {
            throw new BusinessException("登录用户名不能为空！");
        }
        if (userName.Length > 64)
        {
            throw new BusinessException("登录用户名过长（最多 64 个字符）！");
        }
        if (userName == HttpContextExtension.OpenAppTokenName)
        {
            throw new BusinessException("不能使用保留的外部凭据主体名作为登录账号！");
        }
        if (passWord.Length < MinPasswordLength)
        {
            throw new BusinessException($"登录密码不能为空且至少 {MinPasswordLength} 位！");
        }
        var dbType = NormalizeDbType(request.DBType);
        var dbAddress = dbType == "SQLite"
            ? ValidateSqliteFileName(request.DBAddress, createProbe: false)
            : ValidateMySqlAddress(request.DBAddress);
        var scripts = NormalizeScriptSelection(request.Scripts);

        var setting = SystemConfigHelper.GetSetting();
        if (!SystemConfigHelper.NeedSetup(setting))
        {
            throw new BusinessException("系统已经初始化过了，无需重复初始化！");
        }
        // 与本次启动生效的库一致才立即建任务；换库需重启，勾选清单暂存配置跨重启补种
        var dbUnchanged = IsSameDatabase(setting, dbType, dbAddress);

        var taskCount = 0;
        if (dbUnchanged)
        {
            // 先建任务后写配置：播种失败时配置保持未初始化，向导可修正后重试
            taskCount = SeedSelectedTasks(scripts);
        }

        long userTokenNotBefore = 0;
        SystemConfigHelper.UpdateSetting(current =>
        {
            if (!SystemConfigHelper.NeedSetup(current))
            {
                throw new BusinessException("系统已经初始化过了，无需重复初始化！");
            }
            current.UserName = userName;
            current.PassWord = passWord;
            current.DBType = dbType;
            current.DBAddress = dbAddress;
            current.SetupCompleted = true;
            // null = 无待办；换库时写入清单（可为空串 = 明确不建任务），下次启动落新库
            current.SetupPendingScripts = dbUnchanged ? null : string.Join(';', scripts);
            // 与改密同款抬升令牌下限：作废此前签发的一切登录令牌
            current.UserTokenNotBefore = Math.Max(DateTimeOffset.Now.ToUnixTimeSeconds() + 1,
                Math.Max(current.UserTokenNotBefore, current.ManagerTokenNotBefore) + 1);
            current.ManagerTokenNotBefore = current.UserTokenNotBefore;
            userTokenNotBefore = current.UserTokenNotBefore;
        });
        Consts.UserTokenNotBefore = userTokenNotBefore;

        LogServiceHelper.Logs.Enqueue(new LogModel
        {
            CreateTime = DateTime.Now,
            LogType = LogType.操作日志,
            Operator = userName,
            Remark = $"初始化向导完成：数据库 {dbType}，创建任务 {taskCount} 个" + (dbUnchanged ? "" : "（切换数据库，重启后生效）"),
            Success = true,
            Title = "初始化系统"
        });

        return new SetupCompleteResult
        {
            RestartRequired = !dbUnchanged,
            TaskCount = taskCount,
            Message = dbUnchanged
                ? $"初始化完成，已创建 {taskCount} 个任务（停用状态，配置环境变量后可启用）"
                : $"初始化完成：数据库已切换为 {dbType}，需重启服务生效，重启后自动创建 {scripts.Count} 个任务"
        };
    }

    /// <summary>
    /// 按勾选清单把自带脚本登记为停用任务：门禁三级扫描 + Roslyn 编译全部通过后才复制源码/写任务表（原子），
    /// 已登记过的文件名跳过（幂等）。传 null = 全量（存量新库零配置行为）。
    /// 返回新建任务数。
    /// </summary>
    public int SeedSelectedTasks(IReadOnlyCollection<string> fileNames)
    {
        if (fileNames != null && fileNames.All(string.IsNullOrWhiteSpace))
        {
            return 0; // 明确不建任何任务：无需脚本目录
        }
        if (!Directory.Exists(sourceDirectory))
        {
            throw new BusinessException($"任务种子脚本目录不存在：{sourceDirectory}");
        }
        var available = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (available.Count == 0)
        {
            throw new BusinessException($"任务种子脚本目录没有 .cs 文件：{sourceDirectory}");
        }

        List<string> selected;
        if (fileNames == null)
        {
            selected = available;
        }
        else
        {
            // 白名单求交：只接受自带脚本清单内的文件名（防路径穿越、防注册任意外部脚本）
            selected = new List<string>();
            foreach (var name in fileNames)
            {
                var fileName = (name ?? "").Trim();
                if (fileName.Length == 0)
                {
                    continue;
                }
                var match = available.Find(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    throw new BusinessException($"脚本 {fileName} 不在自带脚本清单内，无法初始化！");
                }
                if (!selected.Contains(match, StringComparer.Ordinal))
                {
                    selected.Add(match);
                }
            }
        }

        var existing = db.Tasks.Select(n => n.FileName).ToList();
        var toCreate = selected
            .Where(name => !existing.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // 校验阶段：逐个过门禁+编译，任一不过整体拒绝（不复制文件、不写任务表）
        var validated = new List<(string SourcePath, string FileName, string RuntimePath)>();
        foreach (var fileName in toCreate)
        {
            var sourcePath = Path.Combine(sourceDirectory, fileName);
            var runtimePath = Path.Combine(runtimeDirectory, fileName);
            var content = File.ReadAllText(File.Exists(runtimePath) ? runtimePath : sourcePath);
            var build = ScriptBuildService.Build(content, runtimePath);
            if (!build.Success)
            {
                var issues = build.Blocked.Concat(build.Errors).Select(issue => issue.Message);
                throw new BusinessException($"任务脚本 {fileName} 未通过门禁或编译：{string.Join("；", issues)}");
            }
            validated.Add((sourcePath, fileName, runtimePath));
        }

        // 落盘阶段：源码复制到运行目录（已存在不覆盖，保留用户改动）+ 登记停用任务
        if (validated.Count > 0)
        {
            Directory.CreateDirectory(runtimeDirectory);
        }
        foreach (var (sourcePath, fileName, runtimePath) in validated)
        {
            if (!File.Exists(runtimePath))
            {
                File.Copy(sourcePath, runtimePath);
            }
            db.Tasks.Add(new TaskModel
            {
                Name = Path.GetFileNameWithoutExtension(fileName),
                FileName = fileName,
                CreateTime = DateTime.Now,
                Enable = false,
            });
        }
        db.SaveChanges();
        return validated.Count;
    }

    /// <summary>
    /// 自带脚本文件名白名单化：只接受自带脚本清单内的文件名（防路径穿越/防注册任意外部脚本），
    /// 返回按清单顺序（文件名序）去重后的选择结果——与请求顺序无关，输出确定。
    /// </summary>
    List<string> NormalizeScriptSelection(List<string> scripts)
    {
        var available = ListBundledScripts().Select(n => n.FileName).ToList();
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in scripts ?? new List<string>())
        {
            var fileName = (name ?? "").Trim();
            if (fileName.Length == 0)
            {
                continue;
            }
            if (!available.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new BusinessException($"脚本 {fileName} 不在自带脚本清单内，无法初始化！");
            }
            chosen.Add(fileName);
        }
        return available.Where(chosen.Contains).ToList();
    }

    /// <summary>数据库类型归一：仅接受 SQLite / MySql（兼容 mariadb 别名），其余拒绝。</summary>
    public static string NormalizeDbType(string dbType)
    {
        var value = (dbType ?? "").Trim();
        if (value.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return "SQLite";
        }
        if (value.Equals("mysql", StringComparison.OrdinalIgnoreCase)
            || value.Equals("mariadb", StringComparison.OrdinalIgnoreCase))
        {
            return "MySql";
        }
        throw new BusinessException("数据库类型仅支持 SQLite（本地文件）或 MySql（连接串）！");
    }

    /// <summary>与本次启动生效的库是否一致（一致才可立即建任务，无需重启）。</summary>
    static bool IsSameDatabase(Setting setting, string dbType, string dbAddress)
    {
        try
        {
            return NormalizeDbType(setting.DBType) == dbType
                && string.Equals((setting.DBAddress ?? "").Trim(), dbAddress, StringComparison.OrdinalIgnoreCase);
        }
        catch (BusinessException)
        {
            return false;
        }
    }

    /// <summary>
    /// SQLite 库名校验：只接受 db/ 根内的纯文件名（防目录穿越/子目录拼接）；
    /// createProbe=true 时顺带探针写入验证目录可写（向导「测试连接」用）。
    /// </summary>
    static string ValidateSqliteFileName(string dbAddress, bool createProbe)
    {
        var fileName = (dbAddress ?? "").Trim();
        if (string.IsNullOrEmpty(fileName))
        {
            throw new BusinessException("SQLite 数据库文件名不能为空！");
        }
        if (fileName != Path.GetFileName(fileName) || fileName is "." or "..")
        {
            throw new BusinessException("SQLite 数据库文件名不能包含路径（只允许纯文件名）！");
        }
        if (SafeFile.Resolve("db", fileName) == null)
        {
            throw new BusinessException("SQLite 数据库文件名不合法！");
        }
        if (createProbe)
        {
            try
            {
                Directory.CreateDirectory("db");
                var probe = Path.Combine("db", $".setup-write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "quantum-setup-probe");
                File.Delete(probe);
            }
            catch (Exception e)
            {
                throw new BusinessException($"db 目录不可写：{e.Message}");
            }
        }
        return fileName;
    }

    /// <summary>MySQL 连接串校验：非空即可（可达性由「测试连接」实连验证）。</summary>
    static string ValidateMySqlAddress(string dbAddress)
    {
        var address = (dbAddress ?? "").Trim();
        if (string.IsNullOrEmpty(address))
        {
            throw new BusinessException("MySQL 连接串不能为空（如 Server=127.0.0.1;Database=quantum;Uid=root;Pwd=xxx;）！");
        }
        return address;
    }

    /// <summary>
    /// 脚本用途说明：取头部注释首段（跳过 ==== 分隔行），便于向导里一眼看懂脚本是干嘛的。
    /// </summary>
    internal static string ExtractDescription(string content)
    {
        const int maxLength = 160;
        var builder = new StringBuilder();
        foreach (var rawLine in (content ?? "").Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.StartsWith("//"))
            {
                var text = line.TrimStart('/').Trim();
                // ==== / ---- 分隔行与空注释行不算正文
                if (text.Length == 0 || text.Trim('=', '-', ' ').Length == 0)
                {
                    if (builder.Length > 0)
                    {
                        break; // 正文后的空注释行 = 说明段结束
                    }
                    continue;
                }
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }
                builder.Append(text);
                if (builder.Length >= maxLength)
                {
                    break;
                }
                continue;
            }
            if (line.Length > 0)
            {
                break; // 首个非注释行 = 头部注释结束
            }
        }
        var description = builder.ToString().Trim();
        return description.Length > maxLength ? description[..maxLength].TrimEnd() + "…" : description;
    }
}
