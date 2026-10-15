using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Serialization;
using Quantum.Entities.Config;

namespace Quantum.Utils;

public static class SystemConfigHelper
{
    /// <summary>
    /// 配置文件路径（相对运行目录）：系统配置存于 appsettings.json 的 Quantum 节，
    /// 其余节（Logging/AllowedHosts 等）由宿主与框架使用，写入时原样保留。
    /// </summary>
    public static string configPath = "appsettings.json";

    private const string SectionName = "Quantum";

    /// <summary>
    /// GetSetting 是否同步刷新 Consts（JWT 密钥热更新）。
    /// 消息泵等后台循环会高频调用 GetSetting，测试环境下（配置与用例密钥不一致）
    /// 该刷新会与验签类用例竞态，测试装配通过 ModuleInitializer 关闭。
    /// </summary>
    public static bool ConstsAutoRefresh = true;

    /// <summary>
    /// 读取配置：属性名大小写不敏感（容忍手工编辑时的大小写差异）。
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// 原子替换写入：先写同目录临时文件（WriteThrough 直写磁盘）再 rename 覆盖，任意崩溃/断电时点上
    /// 磁盘的 appsettings.json 都是完整可解析的旧版或新版——杜绝「写一半 → 配置损坏且
    /// JWT 密钥/通道主密钥等不可再生密钥丢失」。写侧全程持 ConfigWriteLock：串行化多个写者，
    /// 消除整文件读改写的丢更新与「A 文本 @ B mtime」缓存错配。
    /// rename 替换不可行时（典型：Docker 单文件 bind-mount 挂载点，Linux rename 固定返回 EBUSY，
    /// 非瞬时占用、重试无解）降级为原地 WriteThrough 直写——挂载文件可直写且写穿宿主，
    /// 否则 Web 端保存设置/改密/向导完成在该部署形态下永远失败。降级路径失去「任意崩溃时点
    /// 文件完整」的原子保证（极端崩溃可能半写），原地写失败照常抛错，绝不静默丢更新。
    /// </summary>
    public static void SetSetting(Setting config)
        => SetSetting(config, configPath);

    private static void SetSetting(Setting config, string path)
    {
        lock (ConfigWriteLock)
        {
            if (config.UserName == HttpContextExtension.OpenAppTokenName)
            {
                throw new InvalidOperationException("登录账号不能使用 Open 凭据的保留名称");
            }
            var root = LoadRoot(path, ReadConfigText(path)) ?? NewSkeleton(config);
            var persisted = root[SectionName]?.Deserialize<Setting>(ReadOptions);
            var notBefore = Math.Max(
                Math.Max(config.UserTokenNotBefore, config.ManagerTokenNotBefore),
                Math.Max(persisted?.UserTokenNotBefore ?? 0, persisted?.ManagerTokenNotBefore ?? 0));
            config.UserTokenNotBefore = notBefore;
            config.ManagerTokenNotBefore = notBefore;
            root[SectionName] = JsonSerializer.SerializeToNode(config);
            var text = root.ToJsonString(WriteOptions);
            var replaced = false;
            for (var attempt = 0; attempt < 3 && !replaced; attempt++)
            {
                var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(text);
                    }
                    MoveFile(tempPath, path);
                    replaced = true;
                }
                // Windows 下替换撞上共享占用按错误码可能落到 UnauthorizedAccessException 而非 IOException，
                // 两者都按瞬时占用重试，不让文件锁竞态冒泡成配置保存失败
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // 瞬时占用（并发读/杀软扫描）短暂重试；挂载点 EBUSY 等永久性失败重试穷尽后走降级直写
                    try { File.Delete(tempPath); } catch { /* 清理失败不影响后续处理 */ }
                    if (attempt < 2)
                    {
                        Thread.Sleep(50);
                    }
                }
            }
            if (!replaced)
            {
                // 持 ConfigTextLock 与读者互斥：读者 ReadAllText 持 FileShare.Read（不放写共享），
                // 锁外直写会与并发读撞车；锁序恒为 ConfigWriteLock → ConfigTextLock，无死锁环
                lock (ConfigTextLock)
                {
                    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(text);
                    }
                }
            }
            // 写后同步文本缓存：消除「缓存仍持旧文本，而磁盘 mtime 恰与写前相同（同 tick 写入）」的读旧窗口
            lock (ConfigTextLock)
            {
                _cachedText = text;
                _cachedPath = path;
                _cachedWriteUtc = File.GetLastWriteTimeUtc(path);
            }
        }
    }

    /// <summary>
    /// 临时文件 rename 覆盖目标；测试可注入「rename 永久不可行」（模拟挂载点 EBUSY）走降级路径。
    /// </summary>
    private static void MoveFile(string tempPath, string path)
    {
        if (TestHookMoveFile != null)
        {
            TestHookMoveFile(tempPath, path);
        }
        else
        {
            File.Move(tempPath, path, true);
        }
    }

    /// <summary>在同一写锁内读取并修改配置，防止并发设置保存覆盖刚改过的登录凭据。</summary>
    public static void UpdateSetting(Action<Setting> update)
    {
        lock (ConfigWriteLock)
        {
            var current = GetSetting();
            update(current);
            SetSetting(current);
        }
    }

    /// <summary>
    /// 是否需要走首次部署初始化向导：显式标记未完成（SetupCompleted=false），
    /// 或登录凭据为空（appsettings.example.json 留空账号密码 = 主动走向导）。
    /// SetupCompleted 缺省（null）按已完成处理——存量部署升级不得被向导劫持。
    /// </summary>
    public static bool NeedSetup(Setting config)
        => config == null
           || config.SetupCompleted == false
           || string.IsNullOrEmpty(config.UserName)
           || string.IsNullOrEmpty(config.PassWord);

    public static Setting GetSetting()
    {
        // 读-改-写全程持写锁（2026-10-10 并发加固）：自动回填走的 SetSetting 与并发写者互斥，
        // 杜绝「锁外读到旧快照 → 回填写回」把并发写者刚落盘的更新（如登录凭据）覆盖掉的丢更新；
        // 同时所有配置文件读访问（ReadConfigText/LoadRoot）都收进锁内，File.Move 替换不再撞上并发读者。
        // 锁可重入（内部 SetSetting 同锁），固定 ConfigWriteLock → ConfigTextLock 加锁顺序，无死锁环。
        lock (ConfigWriteLock)
        {
            return GetSettingCore();
        }
    }

    private static Setting GetSettingCore()
    {
        // §1-7：文本未变（ReadConfigText 命中缓存返回同一 string 引用）→ 复用解析结果，跳过 JsonNode.Parse/Deserialize。
        // 返回克隆而非缓存实例本身：调用方会就地修改返回对象（脱敏/改密），共享会污染权威缓存。
        var path = configPath;
        var currentText = ReadConfigText(path);
        var memo = _memo;
        if (currentText != null && memo != null && memo.Path == path && memo.Setting != null
            && ReferenceEquals(memo.Text, currentText))
        {
            return memo.Setting.Clone();
        }

        // 单源一致性（2026-10-11）：解析的就是本次读到的 currentText，不在 helper 内二次读取——
        // 二次读取会在「两次读取之间恰有写者换文本」时把新文本与旧解析结果配成一组。
        var config = LoadRoot(path, currentText)?[SectionName]?.Deserialize<Setting>(ReadOptions);
        if (config == null)
        {
            // 首次部署（无配置文件/无 Quantum 节）：只生成机器侧密钥与默认 SQLite 库，
            // 登录账号密码留给 Web 初始化向导由用户自建（SetupCompleted=false 期间登录口明确报「未初始化」）
            config = new Setting
            {
                AppKey = null,
                DBAddress = $"Quantum-{RandomStringBuilder.Create(10)}.db",
                DBType = "SQLite",
                PassWord = "",
                UserName = "",
                SetupCompleted = false,
                Port = 5088,
                Host = "http://*"
            };
            Console.WriteLine($"系统基础配置自动生成完成（首次部署未初始化）。");
            Console.WriteLine($"请打开 Web 管理端完成初始化向导：创建登录账号、选择数据库、勾选自带脚本建任务。");
            Console.WriteLine($"数据库类型：{config.DBType}（向导中可切换为 MySQL）");
            Console.WriteLine($"数据库文件：{config.DBAddress}");
            Console.WriteLine($"访问地址：{config.Host}:{config.Port}，请手动替换localhost为您的IP地址。");
            SetSetting(config, path);
        }
        #region  配置文件容错处理
        if (config.Port <= 0)
        {
            config.Port = 5088;
            SetSetting(config, path);
        }

        if (config.CommandTimeInterval < 0)
        {
            config.CommandTimeInterval = 3;
            SetSetting(config, path);
        }

        if (config.MaxConcurrentTasks <= 0)
        {
            config.MaxConcurrentTasks = 8;
            SetSetting(config, path);
        }

        if (config.MessageQueueInterval < 0)
        {
            config.MessageQueueInterval = 100;
            SetSetting(config, path);
        }

        if (string.IsNullOrEmpty(config.DBAddress))
        {
            config.DBAddress = $"Quantum-{RandomStringBuilder.Create(8)}.db";
            SetSetting(config, path);
        }

        if (string.IsNullOrEmpty(config.Host))
        {
            config.Host = "http://*";
            SetSetting(config, path);
        }
        if (string.IsNullOrEmpty(config.DBType))
        {
            config.DBType = "SQLite";
            SetSetting(config, path);
        }
        // 登录凭据不做自动回填（2026-10-10 初始化向导）：留空 = 交由 Web 初始化向导引导用户自建。
        // 此前空凭据会静默生成随机账号密码落盘，用户无从知晓（只能翻配置文件找回），反而挡住向导。
        if (string.IsNullOrEmpty(config.SymmetricSecurityKey))
        {
            config.SymmetricSecurityKey = RandomStringBuilder.Create(64);
            SetSetting(config, path);
        }
        if (string.IsNullOrEmpty(config.SecurityAudience))
        {
            config.SecurityAudience = "Audience." + RandomStringBuilder.Create();
            SetSetting(config, path);
        }
        if (string.IsNullOrEmpty(config.SecurityIssuer))
        {
            config.SecurityIssuer = "Issuer." + RandomStringBuilder.Create();
            SetSetting(config, path);
        }
        if (string.IsNullOrEmpty(config.ChannelMasterKey))
        {
            // 消息通道主密钥：32 字节随机数 Base64 落盘，一次生成终身使用（丢钥=已存凭据全部作废），
            // 生成路径与 SymmetricSecurityKey 同款；环境变量 QUANTUM_CHANNEL_KEY_FILE/MASTER_KEY 存在时运行期不读本值。
            config.ChannelMasterKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            SetSetting(config, path);
        }
        if (config.UserName == HttpContextExtension.OpenAppTokenName)
        {
            throw new InvalidOperationException("登录账号不能使用 Open 凭据的保留名称");
        }
        if (ConstsAutoRefresh)
        {
            // §1-8：单次换入四字段快照，避免读者在四次赋值之间看到跨字段撕裂的密钥组合
            Consts.SetJwtSecrets(config.SymmetricSecurityKey, config.SecurityAudience,
                config.SecurityIssuer, Math.Max(config.UserTokenNotBefore, config.ManagerTokenNotBefore));
        }

        #endregion

        // 只在「文本仍是本轮解析的那一份」时发布 memo：Text 与 Setting 必须出自同一快照，
        // 否则快速路径的 ReferenceEquals(memo.Text, currentText) 会把新文本判成命中、长期返回旧解析结果
        // （该结果喂给 Consts.SetJwtSecrets，即改密钥后旧签名密钥一直生效）。
        // TestHookBeforeMemoPublish：单测在「解析完成、发布之前」插入写者换文本的确定性交错点（生产 null）。
        TestHookBeforeMemoPublish?.Invoke();
        lock (ConfigTextLock)
        {
            if (_cachedPath == path && ReferenceEquals(_cachedText, currentText))
            {
                _memo = new ParsedConfig { Path = path, Text = currentText, Setting = config };
            }
        }
        return config.Clone();
    }

    /// <summary>
    /// 无配置文件时的新建骨架：Quantum 节 + 宿主所需的 Logging/AllowedHosts。
    /// </summary>
    private static JsonObject NewSkeleton(Setting config) => new()
    {
        ["Logging"] = new JsonObject
        {
            ["LogLevel"] = new JsonObject
            {
                ["Default"] = "Warning",
                ["Microsoft.AspNetCore"] = "Warning"
            }
        },
        ["AllowedHosts"] = "*",
        [SectionName] = JsonSerializer.SerializeToNode(config)
    };

    /// <summary>
    /// 配置文本缓存（路径 + mtime 短路）：消息泵/入站队列等常驻循环每 100~200ms 读一次配置，
    /// 旧实现每次全量读盘 + JsonNode 解析（终身的文件 IO 与分配 churn）。
    /// 只缓存文本不缓存解析树——JsonNode 非线程安全，并发调用各建新树避免共享可变状态。
    /// </summary>
    /// <summary>写侧互斥：串行化 SetSetting 的整文件读改写（读侧 ConfigTextLock 只保护读者，不保护写者之间的竞态）。</summary>
    private static readonly object ConfigWriteLock = new();
    private static readonly object ConfigTextLock = new();
    private static string _cachedText;
    private static string _cachedPath;
    private static DateTime _cachedWriteUtc;

    /// <summary>
    /// §1-7：已解析 Setting 按「缓存文本引用」memoize——ReadConfigText 命中时返回同一 string 实例，
    /// 引用相等即文本未变，直接复用上轮解析+容错结果，跳过 JsonNode.Parse/Deserialize（消息泵每 100ms 读一次）。
    /// </summary>
    private static volatile ParsedConfig _memo;

    /// <summary>
    /// 观察点（单测专用）：memo 发布前的缝隙回调，用于确定性构造「解析完成、发布之前文本被换掉」的并发交错；
    /// 默认 null，生产路径零开销、行为不变。
    /// </summary>
    internal static Action TestHookBeforeMemoPublish;

    /// <summary>
    /// 观察点（单测专用）：替换 SetSetting 内 rename 覆盖的执行体，注入抛 IOException 即可确定性模拟
    /// 「目标不可 rename」（Docker 单文件 bind-mount 挂载点的 EBUSY）；默认 null 走真实 File.Move。
    /// </summary>
    internal static Action<string, string> TestHookMoveFile;

    /// <summary>观察点（单测专用）：当前 memo 的文本引用与设置快照（断言 Text 与 Setting 同源）。默认 null 时不变。</summary>
    internal static (string Text, Setting Setting)? InspectMemoForTest()
        => _memo is { } memo ? (memo.Text, memo.Setting) : null;

    private sealed class ParsedConfig
    {
        public string Path;
        public string Text;
        public Setting Setting;
    }

    private static string ReadConfigText(string path)
    {
        lock (ConfigTextLock)
        {
            if (!File.Exists(path))
            {
                _cachedText = null;
                return null;
            }
            var writeUtc = File.GetLastWriteTimeUtc(path);
            if (_cachedText != null && _cachedPath == path && _cachedWriteUtc == writeUtc)
            {
                return _cachedText;
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    // §2-14 TOCTOU：读前后各 stat 一次，两次 mtime 一致才认定「文本」与「时间戳」同属一个落盘版本；
                    // 不一致说明 ReadAllText 期间文件被改写（缓存会把半成品文本钉死到下次同 mtime 命中），
                    // 按未命中重读，最多重试两次后接受最终态兜底。
                    var writeBefore = File.GetLastWriteTimeUtc(path);
                    var text = File.ReadAllText(path);
                    var writeAfter = File.GetLastWriteTimeUtc(path);
                    if (writeBefore != writeAfter && attempt < 2)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    _cachedText = text;
                    _cachedPath = path;
                    _cachedWriteUtc = writeAfter;
                    return text;
                }
                catch (FileNotFoundException)
                {
                    _cachedText = null;
                    return null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 2)
                {
                    // 并发写/杀软扫描的瞬时占用（含 Windows 共享冲突落成的 UnauthorizedAccessException）：短暂重试
                    Thread.Sleep(50);
                }
            }
        }
    }

    /// <summary>
    /// 读取配置文件根节点；文件缺失返回 null（由调用方走自动生成），
    /// 内容不是 JSON 对象则 fail-fast（带病配置不自动覆盖）。
    /// text 为调用方已读到的文本（null = 文件不存在）：解析的就是这一份，不再二次读取，
    /// 杜绝「两次读取之间文本被换掉」导致的解析结果与文本错配。
    /// </summary>
    private static JsonObject LoadRoot(string path, string text)
    {
        if (text == null)
        {
            return null;
        }
        JsonObject root;
        try
        {
            root = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            // 空文件/残缺 JSON（历史版本写一半崩溃的遗留）与「合法 JSON 但非对象」走同一条 fail-fast，
            // 给出同样的可操作提示，而不是抛裸 JsonException
            root = null;
        }
        return root ?? throw new InvalidOperationException($"配置文件 {path} 不是 JSON 对象，请修复或删除后重启自动生成。");
    }
}

public static class SystemCommandHelper
{
    private static readonly string commandsPath = "config/Commands.xml";
    public static void Set(List<SystemCommand> commands)
    {
        XmlHelper<List<SystemCommand>>.Set(commands, commandsPath);
    }
    public static List<SystemCommand> Get()
    {
        List<SystemCommand> commands = null;
        if (commands == null || commands.Count == 0)
        {
            commands =
            [
                new()
                {
                    Key = "重新分配",
                    Command = "重新分配",
                    Tips = "重置分配未指定的环境变量"
                },
                new()
                {
                    Key = "重置分配",
                    Command = "重置分配",
                    Tips = "重置分配所有环境变量，包含手动指定容器的"
                },
                new()
                {
                    Key = "我的量子",
                    Command = "我的量子",
                }
            ];
        }
        return commands;
    }
}

public class SystemCommand
{
    public string Key { get; set; }

    public string Command { get; set; }

    public string Tips { get; set; }
}

public static class XmlHelper<T>
{
    public static T Get(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }
        XmlSerializer serializer = new(typeof(T));
        using (StreamReader reader = new(path))
        {
            var config = (T)serializer.Deserialize(reader);
            return config;
        }
    }

    public static void Set(T config, string path)
    {
        XmlSerializer serializer = new(config.GetType());
        string content = string.Empty;
        using (StringWriter writer = new())
        {
            serializer.Serialize(writer, config);
            content = writer.ToString();
        }
        using (StreamWriter stream_writer = new(path))
        {
            stream_writer.Write(content);
        }
    }
}
