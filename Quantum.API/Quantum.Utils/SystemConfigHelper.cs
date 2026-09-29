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
            var root = LoadRoot(path) ?? NewSkeleton(config);
            var persisted = root[SectionName]?.Deserialize<Setting>(ReadOptions);
            var notBefore = Math.Max(
                Math.Max(config.UserTokenNotBefore, config.ManagerTokenNotBefore),
                Math.Max(persisted?.UserTokenNotBefore ?? 0, persisted?.ManagerTokenNotBefore ?? 0));
            config.UserTokenNotBefore = notBefore;
            config.ManagerTokenNotBefore = notBefore;
            root[SectionName] = JsonSerializer.SerializeToNode(config);
            var text = root.ToJsonString(WriteOptions);
            for (var attempt = 0; ; attempt++)
            {
                var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(text);
                    }
                    File.Move(tempPath, path, true);
                    // 写后同步文本缓存：消除「缓存仍持旧文本，而磁盘 mtime 恰与写前相同（同 tick 写入）」的读旧窗口
                    lock (ConfigTextLock)
                    {
                        _cachedText = text;
                        _cachedPath = path;
                        _cachedWriteUtc = File.GetLastWriteTimeUtc(path);
                    }
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    // 并发读/杀软扫描的瞬时占用：短暂重试（消息泵每百毫秒读一次配置，写侧偶发撞车）
                    try { File.Delete(tempPath); } catch { /* 清理失败不影响重试 */ }
                    Thread.Sleep(50);
                }
            }
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

    public static Setting GetSetting()
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

        var config = LoadRoot(path)?[SectionName]?.Deserialize<Setting>(ReadOptions);
        if (config == null)
        {
            config = new Setting
            {
                AppKey = null,
                DBAddress = $"Quantum-{RandomStringBuilder.Create(10)}.db",
                DBType = "SQLite",
                PassWord = RandomStringBuilder.Create(),
                UserName = "Quantum" + RandomStringBuilder.Create(6),
                Port = 5088,
                Host = "http://*"
            };
            Console.WriteLine($"系统基础配置自动生成完成。");
            Console.WriteLine($"您的用户名：{config.UserName}");
            Console.WriteLine($"您的密码：{config.PassWord}");
            Console.WriteLine($"数据库类型：{config.DBType}");
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
        if (string.IsNullOrEmpty(config.PassWord))
        {
            config.PassWord = RandomStringBuilder.Create();
            SetSetting(config, path);
        }
        if (string.IsNullOrEmpty(config.UserName))
        {
            config.UserName = "Quantum" + RandomStringBuilder.Create(6);
            SetSetting(config, path);
        }
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

        lock (ConfigTextLock)
        {
            if (_cachedPath == path)
            {
                _memo = new ParsedConfig { Path = path, Text = _cachedText, Setting = config };
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
                catch (IOException) when (attempt < 2)
                {
                    // 并发写/杀软扫描的瞬时占用：短暂重试
                    Thread.Sleep(50);
                }
            }
        }
    }

    /// <summary>
    /// 读取配置文件根节点；文件缺失返回 null（由调用方走自动生成），
    /// 内容不是 JSON 对象则 fail-fast（带病配置不自动覆盖）。
    /// </summary>
    private static JsonObject LoadRoot(string path)
    {
        var text = ReadConfigText(path);
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
