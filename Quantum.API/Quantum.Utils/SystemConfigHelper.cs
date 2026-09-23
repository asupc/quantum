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

    public static void SetSetting(Setting config)
    {
        var root = LoadRoot() ?? NewSkeleton(config);
        root[SectionName] = JsonSerializer.SerializeToNode(config);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var text = root.ToJsonString(WriteOptions);
                File.WriteAllText(configPath, text);
                // 写后同步文本缓存：消除「缓存仍持旧文本，而磁盘 mtime 恰与写前相同（同 tick 写入）」的读旧窗口
                lock (ConfigTextLock)
                {
                    _cachedText = text;
                    _cachedPath = configPath;
                    _cachedWriteUtc = File.GetLastWriteTimeUtc(configPath);
                }
                break;
            }
            catch (IOException) when (attempt < 2)
            {
                // 并发读/杀软扫描的瞬时占用：短暂重试（消息泵每百毫秒读一次配置，写侧偶发撞车）
                Thread.Sleep(50);
            }
        }
    }

    public static Setting GetSetting()
    {
        // §1-7：文本未变（ReadConfigText 命中缓存返回同一 string 引用）→ 复用解析结果，跳过 JsonNode.Parse/Deserialize。
        // 返回克隆而非缓存实例本身：调用方会就地修改返回对象（脱敏/改密），共享会污染权威缓存。
        var currentText = ReadConfigText();
        var memo = _memo;
        if (currentText != null && memo != null && memo.Setting != null && ReferenceEquals(memo.Text, currentText))
        {
            return memo.Setting.Clone();
        }

        var config = LoadRoot()?[SectionName]?.Deserialize<Setting>(ReadOptions);
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
            SetSetting(config);
        }
        #region  配置文件容错处理
        if (config.Port <= 0)
        {
            config.Port = 5088;
            SetSetting(config);
        }

        if (config.CommandTimeInterval < 0)
        {
            config.CommandTimeInterval = 3;
            SetSetting(config);
        }

        if (config.MaxConcurrentTasks <= 0)
        {
            config.MaxConcurrentTasks = 8;
            SetSetting(config);
        }

        if (config.MessageQueueInterval < 0)
        {
            config.MessageQueueInterval = 100;
            SetSetting(config);
        }

        if (string.IsNullOrEmpty(config.DBAddress))
        {
            config.DBAddress = $"Quantum-{RandomStringBuilder.Create(8)}.db";
            SetSetting(config);
        }

        if (string.IsNullOrEmpty(config.Host))
        {
            config.Host = "http://*";
            SetSetting(config);
        }
        if (string.IsNullOrEmpty(config.DBType))
        {
            config.DBType = "SQLite";
            SetSetting(config);
        }
        if (string.IsNullOrEmpty(config.PassWord))
        {
            config.PassWord = RandomStringBuilder.Create();
            SetSetting(config);
        }
        if (string.IsNullOrEmpty(config.UserName))
        {
            config.UserName = "Quantum" + RandomStringBuilder.Create(6);
            SetSetting(config);
        }
        if (string.IsNullOrEmpty(config.SymmetricSecurityKey))
        {
            config.SymmetricSecurityKey = RandomStringBuilder.Create(64);
            SetSetting(config);
        }
        if (string.IsNullOrEmpty(config.SecurityAudience))
        {
            config.SecurityAudience = "Audience." + RandomStringBuilder.Create();
            SetSetting(config);
        }
        if (string.IsNullOrEmpty(config.SecurityIssuer))
        {
            config.SecurityIssuer = "Issuer." + RandomStringBuilder.Create();
            SetSetting(config);
        }
        if (ConstsAutoRefresh)
        {
            // §1-8：单次换入四字段快照，避免读者在四次赋值之间看到跨字段撕裂的密钥组合
            Consts.SetJwtSecrets(config.SymmetricSecurityKey, config.SecurityAudience,
                config.SecurityIssuer, config.ManagerTokenNotBefore);
        }

        #endregion

        _memo = new ParsedConfig { Text = _cachedText, Setting = config };
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
        public string Text;
        public Setting Setting;
    }

    private static string ReadConfigText()
    {
        lock (ConfigTextLock)
        {
            if (!File.Exists(configPath))
            {
                _cachedText = null;
                return null;
            }
            var writeUtc = File.GetLastWriteTimeUtc(configPath);
            if (_cachedText != null && _cachedPath == configPath && _cachedWriteUtc == writeUtc)
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
                    var writeBefore = File.GetLastWriteTimeUtc(configPath);
                    var text = File.ReadAllText(configPath);
                    var writeAfter = File.GetLastWriteTimeUtc(configPath);
                    if (writeBefore != writeAfter && attempt < 2)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    _cachedText = text;
                    _cachedPath = configPath;
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
    private static JsonObject LoadRoot()
    {
        var text = ReadConfigText();
        if (text == null)
        {
            return null;
        }
        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidOperationException($"配置文件 {configPath} 不是 JSON 对象，请修复或删除后重启自动生成。");
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
                    IsManager = true,
                    Tips = "重置分配未指定的环境变量"
                },
                new()
                {
                    Key = "重置分配",
                    Command = "重置分配",
                    IsManager = true,
                    Tips = "重置分配所有环境变量，包含手动指定容器的"
                },
                new()
                {
                    Key = "我的量子",
                    Command = "我的量子",
                    IsManager = true,
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

    public bool IsManager { get; set; }

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
