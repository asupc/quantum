using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace Quantum.Application;

/// <summary>
/// 任务脚本危险性门禁：三级扫描（计划 2.3）+ 实参级连接串检查（2026-10-10 对抗性回归）。
/// ① 语法级粗筛：unsafe/fixed/stackalloc/指针/dynamic；
/// ② 语义级黑名单：经 SemanticModel 解析标识符最终绑定符号，按符号所属命名空间/类型/成员判定
///    ——别名、var、using 改名均无法绕过；
/// ③ 启发式警告（不阻断）：疑似死循环（未引用取消令牌）、硬编码 URL。
/// ④ 实参级检查：符号级看不到实参**值**（如 SqliteConnection 的文件库连接串），
///    故对「连接串类」调用在语法上核对字符串字面量，只放行含 :memory: 内存库形态。
/// 定位是防误用 + 粗粒度拦截，不是对抗性沙箱；真正的权限边界仍是 LoggedInUser 上传。
/// </summary>
public static partial class ScriptSecurityGate
{
    /// <summary>单个诊断条目（blocked/warning/error 三类共用）。</summary>
    public sealed class Issue
    {
        public int Line { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
    }

    /// <summary>门禁扫描结果（仅含 ①② 的 blocked 与 ③ 的 warnings）。</summary>
    public sealed class GateResult
    {
        public List<Issue> Blocked { get; } = new();
        public List<Issue> Warnings { get; } = new();
        public bool Passed => Blocked.Count == 0;
    }

    // ② 语义黑名单：命名空间前缀（命中该命名空间或其子命名空间/内部类型均拦截）
    private static readonly (string Prefix, string Reason)[] NamespaceBlacklist =
    {
        ("System.IO", "文件系统访问被禁止：任务数据与产物走平台服务（ctx.Env/ctx.Notify/ctx.Log）"),
        ("System.Reflection", "反射被禁止：防运行时动态加载黑名单类型绕过门禁"),
        ("System.Runtime.Loader", "程序集加载上下文被禁止：防运行时动态加载绕过门禁"),
        ("System.Runtime.InteropServices", "互操作被禁止：防 native 逃逸（Marshal/GCHandle/P-Invoke/DllImport）"),
        ("Microsoft.CSharp", "Microsoft.CSharp 被禁止：dynamic 绑定底层"),
        ("Microsoft.Win32", "Microsoft.Win32 被禁止：注册表等系统面访问"),
        ("System.Net.Sockets", "裸 socket 被禁止：外部网络请求请走 ctx.Http（预配代理/超时）"),
        ("System.Threading.Thread", "Thread 直接操作被禁止：任务并发请用 Task/async，取消走 ct"),
        ("System.Linq.Expressions", "表达式树被禁止：防运行时编译代码绕过门禁"),
        // ---- 2026-10-10 对抗性回归：下列命名空间在自带脚本中零使用，按前缀整段拦截 ----
        //   判定依据：System.IO 只拦了自身前缀，同族 BCL（XML/资源/Tar）都能替代它读写任意路径文件；
        //   System.Net 前缀不可整段拦（WebUtility/HttpStatusCode/Http.Headers 是自带脚本既有能力），
        //   故其下的裸通道按类型拦截（见 TypeBlacklist）。
        ("System.Xml", "XML 读写被禁止：XmlWriter/XDocument/XmlDocument/XmlSerializer 等可替代 System.IO 读写任意文件"),
        ("System.Resources", "资源读写被禁止：ResourceWriter/ResourceReader 可替代 System.IO 读写任意文件"),
        ("System.Formats.Tar", "归档读写被禁止：TarFile 可解包/打包任意路径文件（acme_cert 用的 System.Formats.Asn1 不受影响）"),
        ("System.Net.Mail", "SMTP 被禁止：裸邮件通道请改走平台通知（ctx.Notify）"),
        ("System.Net.NetworkInformation", "网络探测被禁止：Ping 等可扫描/探测内网（外部请求请走 ctx.Http）"),
        ("System.Net.WebSockets", "裸 WebSocket 被禁止：外部网络通信请走 ctx.Http"),
        ("System.Diagnostics.EventLog", "事件日志被禁止：本机系统面访问"),
        ("System.Drawing", "图形/GDI 被禁止：可读写任意文件与调用系统面（任务无需图片处理能力）"),
        ("System.ComponentModel", "组件模型被禁止：TypeDescriptor/PropertyDescriptor 可按名读写任意对象属性（反射桥替补）"),
        ("Microsoft.VisualBasic", "Visual Basic 运行时被禁止：FileIO.FileSystem 可替代 System.IO 读写任意文件"),
    };

    // ② 语义黑名单：精确类型全名
    private static readonly (string TypeName, string Reason)[] TypeBlacklist =
    {
        ("System.Diagnostics.Process", "进程操作被禁止：防「配置即执行」开口子"),
        ("System.Diagnostics.ProcessStartInfo", "进程操作被禁止：防「配置即执行」开口子"),
        ("System.Environment", "System.Environment 被禁止（含 Exit/FailFast，防脚本杀宿主）"),
        ("System.AppDomain", "AppDomain 被禁止"),
        ("System.Activator", "Activator 被禁止：防运行时动态实例化绕过门禁"),
        ("System.Console", "输出必须走 ctx.Log（统一实时日志与落盘）"),
        // ---- 2026-10-10 对抗性回归：文件/磁盘/网络/调度/调试面（命名空间前缀不可整段拦或无需整段拦）----
        //   System.Xml / System.Resources / System.Formats.Tar / System.Diagnostics.EventLog 已在命名空间前缀表整段拦，
        //   此处只列「父命名空间不可整段拦」的精确类型（System.Diagnostics 有 Stopwatch、System.Net 有 WebUtility 等既有能力）
        ("System.Net.WebClient", "WebClient 被禁止：DownloadFile/UploadFile 可读写任意路径文件（外部请求请走 ctx.Http）"),
        ("System.Net.HttpListener", "HttpListener 被禁止：可在宿主内开监听端口（外部通信请走 ctx.Http）"),
        ("System.Net.Dns", "Dns 被禁止：可按名解析/探测内网主机（属裸网络通道，外部请求请走 ctx.Http）"),
        ("System.Diagnostics.TextWriterTraceListener", "Trace 监听器被禁止：构造即按路径写文件（写入任意路径）"),
        ("System.Diagnostics.DelimitedListTraceListener", "Trace 监听器被禁止：构造即按路径写文件（写入任意路径）"),
        ("System.Diagnostics.XmlWriterTraceListener", "Trace 监听器被禁止：构造即按路径写文件（写入任意路径）"),
        ("System.Diagnostics.Debugger", "调试器接管被禁止：Launch/Break 可挂起或接管宿主进程"),
        ("System.Diagnostics.FileVersionInfo", "文件版本信息被禁止：GetVersionInfo 按路径读取任意文件元数据"),
        ("System.Threading.Timer", "Timer 被禁止：回调在运行记录关闭后仍可能继续执行（防越权续跑）"),
        ("System.Timers.Timer", "Timer 被禁止：回调在运行记录关闭后仍可能继续执行（防越权续跑）"),
        ("System.Threading.ThreadPool", "ThreadPool 被禁止：后台排队执行会在运行记录关闭后继续跑（并发请用 Task/async + ct）"),
        // 内存/运行时底层面：现状只拦了 System.Runtime.Loader / System.Runtime.InteropServices 两个前缀，
        // 同命名空间下的其它运行时后门（内存直接改写、绕过构造初始化）不在既有规则覆盖内
        ("System.Runtime.CompilerServices.Unsafe", "Unsafe 被禁止：可读写任意内存地址（绕过一切托管边界）"),
        ("System.Runtime.CompilerServices.RuntimeHelpers", "RuntimeHelpers 被禁止：GetUninitializedObject 等可绕过构造初始化与门禁实例化约束"),
        ("Microsoft.Data.Sqlite.SqliteFactory", "SqliteFactory 被禁止：可动态创建连接绕开构造实参检查（内存库请直接 new SqliteConnection(\"Data Source=:memory:\"）"),
        ("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder", "SqliteConnectionStringBuilder 被禁止：可按名拼装连接串绕开字面量检查"),
    };

    // ② 语义黑名单：精确成员（类型全名 + 成员名）
    private static readonly (string TypeName, string Member, string Reason)[] MemberBlacklist =
    {
        ("System.Type", "GetType", "Type.GetType(string) 按名加载类型被禁止：防绕过门禁加载黑名单类型"),
        // ---- 2026-10-10 对抗性回归：System.Type 是「取任意类型 → 取其任意成员」的能力桥 --------
        // 根因：QualificationNames 只看符号自身归属链，System.Type 的成员归属链是 System 命名空间，
        // 命名空间前缀规则对它不可见（如 t.GetMethod("ReadAllText") 可拿回 System.IO.File 的方法）。
        ("System.Type", "GetMethod", "Type.GetMethod 被禁止：拿回任意方法后可经 Delegate.CreateDelegate 调用（反射桥）"),
        ("System.Type", "GetMethods", "Type.GetMethods 被禁止：反射桥（枚举任意方法）"),
        ("System.Type", "GetConstructor", "Type.GetConstructor 被禁止：反射桥（拿回构造器绕过 new）"),
        ("System.Type", "GetConstructors", "Type.GetConstructors 被禁止：反射桥（拿回构造器绕过 new）"),
        ("System.Type", "GetProperties", "Type.GetProperties 被禁止：反射桥（枚举任意属性）"),
        ("System.Type", "GetProperty", "Type.GetProperty 被禁止：反射桥（拿回属性描述后用 SetValue/GetValue 读写任意对象）"),
        ("System.Type", "GetFields", "Type.GetFields 被禁止：反射桥（枚举任意字段）"),
        ("System.Type", "GetField", "Type.GetField 被禁止：反射桥（拿回字段描述后用 SetValue/GetValue 读写任意对象）"),
        ("System.Type", "GetMembers", "Type.GetMembers 被禁止：反射桥（枚举任意成员）"),
        ("System.Type", "GetMember", "Type.GetMember 被禁止：反射桥（枚举任意成员）"),
        ("System.Type", "GetNestedType", "Type.GetNestedType 被禁止：反射桥（拿回嵌套类型）"),
        ("System.Type", "GetNestedTypes", "Type.GetNestedTypes 被禁止：反射桥（枚举嵌套类型）"),
        ("System.Type", "GetInterfaces", "Type.GetInterfaces 被禁止：反射桥（枚举接口）"),
        ("System.Type", "GetInterface", "Type.GetInterface 被禁止：反射桥（拿回接口类型）"),
        ("System.Type", "GetEvents", "Type.GetEvents 被禁止：反射桥（枚举事件）"),
        ("System.Type", "GetEvent", "Type.GetEvent 被禁止：反射桥（拿回事件描述）"),
        ("System.Type", "GetTypeHandle", "Type.GetTypeHandle / TypeHandle 被禁止：可拿回运行时句柄走非托管面"),
        ("System.Type", "TypeHandle", "Type.GetTypeHandle / TypeHandle 被禁止：可拿回运行时句柄走非托管面"),
        ("System.Type", "Assembly", "Type.Assembly 被禁止：拿回程序集后可枚举/加载类型绕过门禁"),
        ("System.Type", "InvokeMember", "Type.InvokeMember 被禁止：按名调用任意成员（反射桥，等价 GetMethod+Invoke）"),
        // System.Delegate 是「方法 → 可调用委托」的桥：DynamicInvoke 与 CreateDelegate 均可越过源级调用形态
        ("System.Delegate", "CreateDelegate", "Delegate.CreateDelegate 被禁止：把任意 MethodInfo 变可调用委托（反射桥出口）"),
        ("System.Delegate", "DynamicInvoke", "Delegate.DynamicInvoke 被禁止：运行期动态调用（反射桥出口）"),
        // ---- 2026-10-10：EF Core 原始 SQL / 连接句柄（编译引用白名单内可达，可篡改平台库）----
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSqlRaw", "EF 原始 SQL 被禁止：直执 SQL 可篡改/删除平台库数据"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSqlRawAsync", "EF 原始 SQL 被禁止：直执 SQL 可篡改/删除平台库数据"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSql", "EF 原始 SQL 被禁止：直执 SQL 可篡改/删除平台库数据"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSqlAsync", "EF 原始 SQL 被禁止：直执 SQL 可篡改/删除平台库数据"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSqlInterpolated", "EF 原始 SQL 被禁止：直执 SQL 可篡改/删除平台库数据"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSqlInterpolatedAsync", "EF 原始 SQL 被禁止：直执 SQL 可篡改/删除平台库数据"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "GetDbConnection", "Database.GetDbConnection 被禁止：拿回原生命令通道可绕开一切上层限制"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "SetDbConnection", "Database.SetDbConnection 被禁止：可换接任意连接（含文件库）"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "GetConnectionString", "Database.GetConnectionString 被禁止：可读出平台连接串（敏感信息外泄面）"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "SetConnectionString", "Database.SetConnectionString 被禁止：可按连接串切到任意文件库"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "Migrate", "Database.Migrate 被禁止：可对平台库执行迁移（改结构）"),
        ("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "MigrateAsync", "Database.MigrateAsync 被禁止：可对平台库执行迁移（改结构）"),
        // EnsureCreated/EnsureDeleted 是 DatabaseFacade 的实例方法（不在 RelationalDatabaseFacadeExtensions 上）
        ("Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade", "EnsureCreated", "Database.EnsureCreated 被禁止：可按连接串建库落盘"),
        ("Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade", "EnsureCreatedAsync", "Database.EnsureCreatedAsync 被禁止：可按连接串建库落盘"),
        ("Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade", "EnsureDeleted", "Database.EnsureDeleted 被禁止：可按连接串删库"),
        ("Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade", "EnsureDeletedAsync", "Database.EnsureDeletedAsync 被禁止：可按连接串删库"),
        ("Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade", "CanConnect", "Database.CanConnect 被禁止：可探测数据文件/连接可达性（敏感面探测）"),
        ("Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade", "CanConnectAsync", "Database.CanConnectAsync 被禁止：可探测数据文件/连接可达性（敏感面探测）"),
        // UseSqlite 不整条拦（内存库形态需保留）：连接串实参由 ④ 实参级规则核对（arg:use-sqlite-memory-only）
        // SqliteConnection.ConnectionString 不拦读（无越权面）；写入由 ④ 实参级规则核对（arg:connection-string-memory-only）
        // SQLite 原生扩展加载是 native 逃逸面（可在进程内加载任意 dll 并注册函数）；
        // BackupDatabase 可把内存库内容复制到文件库（绕过「只允许内存库」的连接限制落盘）
        ("Microsoft.Data.Sqlite.SqliteConnection", "LoadExtension", "LoadExtension 被禁止：可加载任意原生扩展 dll（native 逃逸面）"),
        ("Microsoft.Data.Sqlite.SqliteConnection", "EnableExtensions", "EnableExtensions 被禁止：开启扩展加载能力（配合 LoadExtension 逃逸）"),
        ("Microsoft.Data.Sqlite.SqliteConnection", "BackupDatabase", "BackupDatabase 被禁止：可把内存库内容复制到文件库（绕过内存库连接限制落盘）"),
        // ---- 2026-10-10：HtmlAgilityPack 的文件/网络面（内存解析 LoadHtml/SelectNodes 是自带脚本主力用法，须保留）----
        ("HtmlAgilityPack.HtmlDocument", "Load", "HtmlDocument.Load(路径) 被禁止：按路径读文件（内存 HTML 请用 LoadHtml 字符串重载）"),
        ("HtmlAgilityPack.HtmlDocument", "Save", "HtmlDocument.Save(路径) 被禁止：按路径写文件（如需落盘产物请走 ctx.File）"),
        ("HtmlAgilityPack.HtmlWeb", "Load", "HtmlWeb.Load 被禁止：直接发起网络抓取（外部请求请走 ctx.Http，预配代理/超时）"),
        ("HtmlAgilityPack.HtmlWeb", "Get", "HtmlWeb.Get 被禁止：直接发起网络抓取（外部请求请走 ctx.Http，预配代理/超时）"),
        ("HtmlAgilityPack.HtmlWeb", "LoadFromWebAsync", "HtmlWeb.LoadFromWebAsync 被禁止：直接发起网络抓取（外部请求请走 ctx.Http，预配代理/超时）"),
    };

    // ④ 实参级规则：符号级扫描看不到实参**值**（如连接串字符串），故对下列调用核对语法实参。
    //   规则码与说明同源（DescribeRules 直接读取），避免「提示词说能写、门禁实际拦」的漂移。
    internal const string RuleSqliteMemoryOnly = "arg:sqlite-memory-only";
    internal const string RuleUseSqliteMemoryOnly = "arg:use-sqlite-memory-only";
    internal const string RuleConnectionStringMemoryOnly = "arg:connection-string-memory-only";
    internal const string RuleCommandTextLiteral = "sql:command-text-literal";
    internal const string RuleJsonDeserializeDbConnection = "arg:json-deserialize-dbconn";

    private static readonly (string Code, string Description)[] ArgumentRuleNotes =
    {
        (RuleSqliteMemoryOnly, "SqliteConnection 构造：仅允许「含 :memory: 的字符串字面量」实参（文件库连接可读写任意路径文件、可打开并篡改平台数据库）"),
        (RuleUseSqliteMemoryOnly, "EF UseSqlite：仅允许「含 :memory: 的字符串字面量」实参（按路径建库会写盘）"),
        (RuleConnectionStringMemoryOnly, "ConnectionString 赋值：仅允许「含 :memory: 的字符串字面量」右值（改写连接串等于换接文件库）"),
        (RuleCommandTextLiteral, "SQLite 命令文本：仅允许字符串字面量，且不得含 ATTACH / VACUUM INTO（SQL 文本级写文件，绕过内存库限制）"),
        (RuleJsonDeserializeDbConnection, "JSON 反序列化入口：目标类型不得是数据库连接类型（按名填充 ConnectionString 可绕开源级成员检查）；类型实参须为编译期可见形态（typeof 字面量或泛型实参），运行期取得的 Type 一律拒绝"),
    };

    // ④ 实参级规则涉及的符号（类型全名）
    private const string SqliteConnectionType = "Microsoft.Data.Sqlite.SqliteConnection";
    private const string SqliteCommandType = "Microsoft.Data.Sqlite.SqliteCommand";
    private const string DbConnectionType = "System.Data.Common.DbConnection";
    private const string DbCommandType = "System.Data.Common.DbCommand";
    private const string UseSqliteExtensionsType = "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions";
    private static readonly string[] ConnectionStringOwners = { SqliteConnectionType, DbConnectionType };
    private static readonly string[] CommandTextOwners = { SqliteCommandType, DbCommandType };

    /// <summary>JSON 反序列化/属性灌入入口（目标类型可控 → 反射桥替补）：泛型实参或实参类型指向连接类型即拦截。</summary>
    private static readonly (string TypeName, string[] Members)[] JsonDeserializeApis =
    {
        ("Newtonsoft.Json.JsonConvert", new[] { "DeserializeObject", "PopulateObject" }),
        ("Newtonsoft.Json.JsonSerializer", new[] { "Deserialize", "Populate" }),
        ("Newtonsoft.Json.Linq.JToken", new[] { "ToObject" }),
        ("System.Text.Json.JsonSerializer", new[] { "Deserialize" }),
    };

    [GeneratedRegex(@"https?://[^\s""'<>\\)）、，；]+")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\bATTACH\b|\bVACUUM\s+INTO\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqliteFileSqlRegex();

    /// <summary>
    /// 门禁规则摘要（只读，供 AI Agent 提示词与「AI 已知的平台能力」面板展示）：
    /// 与扫描表同源，规则调整后提示词自动跟随，不会出现「提示词说能写、门禁实际拦」的漂移。
    /// </summary>
    public static IReadOnlyList<string> DescribeRules()
    {
        var lines = new List<string> { "【禁止使用的命名空间】（命中即拦截落盘）" };
        lines.AddRange(NamespaceBlacklist.Select(n => $"- {n.Prefix}：{n.Reason}"));
        lines.Add("【禁止使用的类型】");
        lines.AddRange(TypeBlacklist.Select(n => $"- {n.TypeName}：{n.Reason}"));
        lines.Add("【禁止使用的成员】");
        lines.AddRange(MemberBlacklist.Select(n => $"- {n.TypeName}.{n.Member}：{n.Reason}"));
        lines.Add("【实参级限制（按调用实参/右值核对，不看符号）】");
        lines.AddRange(ArgumentRuleNotes.Select(n => $"- {n.Code}：{n.Description}"));
        lines.Add("【警告（不拦截，但应避免）】");
        lines.Add("- dead-loop：while(true)/无条件 for 且循环体未引用取消令牌 ct");
        lines.Add("- hardcoded-url：硬编码 http(s) 地址且不在 ctx.Http.* 实参位置（应改为读取环境变量）");
        lines.Add("【其他语法限制】unsafe/fixed/stackalloc/指针类型一律拦截");
        return lines;
    }

    /// <summary>
    /// 执行完整三级扫描。语义级（②）依赖外部构建好的 Compilation（与编译服务共用引用集，
    /// 保证「扫的就是编的」）；tree 必须与 compilation 中的一致。
    /// </summary>
    public static GateResult Scan(SyntaxTree tree, CSharpCompilation compilation)
    {
        var result = new GateResult();
        if (tree.GetRoot() is not CompilationUnitSyntax root)
        {
            return result;
        }
        var model = compilation.GetSemanticModel(tree);

        ScanSyntaxLevel(root, result);
        ScanSemanticLevel(root, model, result);
        ScanArgumentLevel(root, model, result);
        ScanHeuristics(root, tree, result);
        return result;
    }

    /// <summary>遍历用户代码节点，但不进入 using 指令——导入声明本身放行，拦的是符号的实际使用
    /// （含隐式 using prelude 的 System.IO；别名/命名空间声明的伪装由使用点的语义绑定兜住）。</summary>
    private static IEnumerable<SyntaxNode> CodeNodes(CompilationUnitSyntax root)
    {
        return root.DescendantNodes(n => n is not UsingDirectiveSyntax);
    }

    /// <summary>① 语法级粗筛：不依赖语义的硬拦截形态。</summary>
    private static void ScanSyntaxLevel(CompilationUnitSyntax root, GateResult result)
    {
        // unsafe/fixed 修饰符（unsafe class / fixed 字段等非语句块形态）经 token 级扫描捕获
        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.UnsafeKeyword))
            {
                AddBlocked(result, token.Parent, "unsafe", "unsafe 代码被禁止");
            }
            else if (token.IsKind(SyntaxKind.FixedKeyword))
            {
                AddBlocked(result, token.Parent, "fixed", "fixed 语句/修饰被禁止");
            }
        }

        foreach (var node in CodeNodes(root))
        {
            // DllImport 等互操作特性由 ② 语义级按 System.Runtime.InteropServices 前缀拦截（更强），此处不重复
            if (node is UnsafeStatementSyntax)
            {
                AddBlocked(result, node, "unsafe", "unsafe 代码被禁止");
            }
            else if (node is FixedStatementSyntax)
            {
                AddBlocked(result, node, "fixed", "fixed 语句被禁止");
            }
            else if (node.IsKind(SyntaxKind.StackAllocArrayCreationExpression))
            {
                AddBlocked(result, node, "stackalloc", "stackalloc 被禁止");
            }
            else if (node is PointerTypeSyntax)
            {
                AddBlocked(result, node, "pointer", "指针类型被禁止");
            }
        }
    }

    /// <summary>
    /// ② 语义级黑名单：解析每个标识符/成员访问最终绑定的符号。
    /// var 推断、using 别名、命名空间改写都逃不过——判定依据是符号的最终归属，而非源码文本。
    /// </summary>
    private static void ScanSemanticLevel(CompilationUnitSyntax root, SemanticModel model, GateResult result)
    {
        // 规则去重：同一规则同一行只报一次，避免一个 using 洗出一屏重复项
        var reported = new HashSet<(string code, int line)>();

        foreach (var node in CodeNodes(root).OfType<SimpleNameSyntax>())
        {
            var info = model.GetSymbolInfo(node);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            // 别名解包：using F = System.IO.File; F.xxx —— F 绑定的是 IAliasSymbol
            while (symbol is IAliasSymbol alias)
            {
                symbol = alias.Target;
            }
            if (symbol == null)
            {
                continue;
            }

            if (symbol is IDynamicTypeSymbol)
            {
                AddBlocked(result, node, "dynamic", "dynamic 关键字被禁止");
                continue;
            }

            // 成员级规则（Type.GetType 这类按名加载形态）
            foreach (var (typeName, member, reason) in MemberBlacklist)
            {
                if (symbol.Name == member && symbol.ContainingType?.ToDisplayString() == typeName)
                {
                    AddOnce(result, reported, node, $"member:{typeName}.{member}", reason, blocked: true);
                }
            }

            // 命名空间/类型级规则：对符号的「归属链」逐层判定
            // （命名空间自身、所属类型、逐级包含命名空间；前缀匹配含子命名空间与内部类型）
            foreach (var candidate in QualificationNames(symbol))
            {
                foreach (var (prefix, reason) in NamespaceBlacklist)
                {
                    if (MatchesPrefix(candidate, prefix))
                    {
                        AddOnce(result, reported, node, $"ns:{prefix}", reason, blocked: true);
                    }
                }
                foreach (var (typeName, reason) in TypeBlacklist)
                {
                    if (candidate == typeName)
                    {
                        AddOnce(result, reported, node, $"type:{typeName}", reason, blocked: true);
                    }
                }
            }
        }
    }

    /// <summary>符号归属链上的全部限定名（类型/命名空间自身、所属类型、各级包含命名空间）。</summary>
    private static IEnumerable<string> QualificationNames(ISymbol symbol)
    {
        if (symbol is INamespaceSymbol ns)
        {
            yield return ns.ToDisplayString();
            yield break;
        }
        // 类型符号自身（直接引用类型的场景：new System.Diagnostics.Process() 里的 Process）
        if (symbol is ITypeSymbol type)
        {
            yield return type.ToDisplayString();
        }
        if (symbol.ContainingType != null)
        {
            yield return symbol.ContainingType.ToDisplayString();
        }
        for (var container = symbol.ContainingNamespace; container != null && !container.IsGlobalNamespace; container = container.ContainingNamespace)
        {
            yield return container.ToDisplayString();
        }
    }

    private static bool MatchesPrefix(string candidate, string prefix)
    {
        return candidate == prefix || candidate.StartsWith(prefix + ".", StringComparison.Ordinal);
    }

    /// <summary>
    /// ④ 实参/右值级检查：符号级规则看不到实参**值**，而活体证据证明
    /// 「:memory: 之外的 SQLite 连接串」「ATTACH / VACUUM INTO 命令文本」「按名填充连接串」
    /// 都能读写任意文件并篡改平台库（执行还报告成功），故此处按语法核对实参/右值：
    /// 连接串只放行内存库形态，SQL 文本只放行不含 ATTACH/VACUUM INTO 的字面量，
    /// JSON 反序列化目标类型不得是数据库连接类型。
    /// 注意：命名空间/类型/成员规则在 ② 已经命中的节点不再重复报（如 m.Invoke(null,null)
    /// 的 m 已由 System.Reflection 前缀拦下）：此处只补 ② 看不到的「实参值」维度。
    /// </summary>
    private static void ScanArgumentLevel(CompilationUnitSyntax root, SemanticModel model, GateResult result)
    {
        // 与 ② 同款去重：同一实参规则同一行只报一次
        var reported = new HashSet<(string code, int line)>();

        foreach (var node in CodeNodes(root))
        {
            switch (node)
            {
                case ObjectCreationExpressionSyntax oce:
                    CheckConnectionConstruction(oce, oce.ArgumentList, model, result, reported);
                    CheckSqliteCommandConstruction(oce, oce.ArgumentList, model, result, reported);
                    break;
                case ImplicitObjectCreationExpressionSyntax ioce:
                    CheckConnectionConstruction(ioce, ioce.ArgumentList, model, result, reported);
                    CheckSqliteCommandConstruction(ioce, ioce.ArgumentList, model, result, reported);
                    break;
                case ConstructorInitializerSyntax ci:
                    CheckConnectionConstruction(ci, ci.ArgumentList, model, result, reported);
                    break;
                case AssignmentExpressionSyntax asn:
                    CheckConnectionStringAssignment(asn, model, result, reported);
                    CheckCommandTextAssignment(asn, model, result, reported);
                    break;
                case InvocationExpressionSyntax inv:
                    CheckUseSqliteInvocation(inv, model, result, reported);
                    CheckJsonDeserializeInvocation(inv, model, result, reported);
                    break;
            }
        }
    }

    /// <summary>数据库连接构造（new / 隐式 new / 派生类 : base(...)）：只放行含 :memory: 的字符串字面量实参。</summary>
    private static void CheckConnectionConstruction(SyntaxNode node, ArgumentListSyntax arguments, SemanticModel model, GateResult result,
        HashSet<(string, int)> reported)
    {
        ITypeSymbol created = node switch
        {
            // 显式/隐式 new：类型信息给出被构造类型（含派生类，派生类可借参数透传连接串）
            ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax => model.GetTypeInfo(node).Type,
            // : base(...)：只看直连 SqliteConnection 的基构造（上游逐层由各自规则覆盖）
            ConstructorInitializerSyntax => (model.GetSymbolInfo(node).Symbol as IMethodSymbol)?.ContainingType,
            _ => null
        };
        if (created == null || !IsOrDerivesFrom(created, SqliteConnectionType))
        {
            return;
        }
        if (arguments != null && ContainsMemoryOnlyLiteral(arguments))
        {
            return;
        }
        AddOnce(result, reported, node, RuleSqliteMemoryOnly, ArgumentReason(RuleSqliteMemoryOnly), blocked: true);
    }

    /// <summary>ConnectionString 赋值：右值必须是含 :memory: 的字符串字面量（读属性不受影响）。</summary>
    private static void CheckConnectionStringAssignment(AssignmentExpressionSyntax asn, SemanticModel model, GateResult result,
        HashSet<(string, int)> reported)
    {
        if (model.GetSymbolInfo(asn.Left).Symbol is not IPropertySymbol prop
            || prop.Name != "ConnectionString"
            || !ConnectionStringOwners.Contains(prop.ContainingType?.ToDisplayString()))
        {
            return;
        }
        if (asn.Right is LiteralExpressionSyntax literal && IsMemoryOnlyLiteral(literal))
        {
            return;
        }
        AddOnce(result, reported, asn, RuleConnectionStringMemoryOnly, ArgumentReason(RuleConnectionStringMemoryOnly), blocked: true);
    }

    /// <summary>SqliteCommand 构造：首个字符串实参即命令文本，ATTACH/VACUUM INTO 与非字面量同样拦（不走属性赋值）。</summary>
    private static void CheckSqliteCommandConstruction(SyntaxNode node, ArgumentListSyntax arguments, SemanticModel model,
        GateResult result, HashSet<(string, int)> reported)
    {
        if (arguments == null || arguments.Arguments.Count == 0)
        {
            return;
        }
        var type = model.GetTypeInfo(node).Type;
        if (!IsOrDerivesFrom(type, SqliteCommandType) && !IsOrDerivesFrom(type, DbCommandType))
        {
            return;
        }
        if (arguments.Arguments[0].Expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression)
            && !SqliteFileSqlRegex().IsMatch(literal.Token.ValueText))
        {
            return;
        }
        AddOnce(result, reported, node, RuleCommandTextLiteral, ArgumentReason(RuleCommandTextLiteral), blocked: true);
    }

    /// <summary>CommandText 赋值：只放行不含 ATTACH / VACUUM INTO 的字符串字面量（SQL 文本级写文件绕过内存库限制）。</summary>
    private static void CheckCommandTextAssignment(AssignmentExpressionSyntax asn, SemanticModel model, GateResult result,
        HashSet<(string, int)> reported)
    {
        if (model.GetSymbolInfo(asn.Left).Symbol is not IPropertySymbol prop
            || prop.Name != "CommandText"
            || !CommandTextOwners.Contains(prop.ContainingType?.ToDisplayString()))
        {
            return;
        }
        if (asn.Right is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            && !SqliteFileSqlRegex().IsMatch(literal.Token.ValueText))
        {
            return;
        }
        AddOnce(result, reported, asn, RuleCommandTextLiteral, ArgumentReason(RuleCommandTextLiteral), blocked: true);
    }

    /// <summary>EF UseSqlite：只放行含 :memory: 的字符串字面量（按路径建库会写盘并使原始 SQL 可达）。</summary>
    private static void CheckUseSqliteInvocation(InvocationExpressionSyntax inv, SemanticModel model, GateResult result,
        HashSet<(string, int)> reported)
    {
        if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol method
            || method.Name != "UseSqlite"
            || method.ContainingType?.ToDisplayString() != UseSqliteExtensionsType)
        {
            return;
        }
        if (ContainsMemoryOnlyLiteral(inv.ArgumentList))
        {
            return;
        }
        AddOnce(result, reported, inv, RuleUseSqliteMemoryOnly, ArgumentReason(RuleUseSqliteMemoryOnly), blocked: true);
    }

    /// <summary>JSON 反序列化：目标类型（泛型实参 / typeof 实参 / 实参静态类型）为数据库连接类型即拦截。
    /// 按名填充 ConnectionString 等属性可完全绕开源级成员检查（无任何被拦符号）。</summary>
    private static void CheckJsonDeserializeInvocation(InvocationExpressionSyntax inv, SemanticModel model, GateResult result,
        HashSet<(string, int)> reported)
    {
        if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol method)
        {
            return;
        }
        var owner = method.ContainingType?.ToDisplayString();
        var api = Array.Find(JsonDeserializeApis, a => a.TypeName == owner && a.Members.Contains(method.Name));
        if (api.TypeName == null)
        {
            return;
        }
        foreach (var typeArgument in method.TypeArguments)
        {
            if (IsOrDerivesFrom(typeArgument, DbConnectionType))
            {
                AddOnce(result, reported, inv, RuleJsonDeserializeDbConnection, ArgumentReason(RuleJsonDeserializeDbConnection), blocked: true);
                return;
            }
        }
        foreach (var argument in inv.ArgumentList.Arguments)
        {
            var staticType = model.GetTypeInfo(argument.Expression).Type;
            // System.Type 实参必须写成 typeof(...) 字面形态：运行期取得的 Type（如 x.GetType()）静态不可判，
            // 否则 JsonConvert.DeserializeObject(json, t) 这类形态可完全绕开源级成员检查（活体验证可换接文件库）
            if (staticType != null && IsOrDerivesFrom(staticType, "System.Type"))
            {
                if (argument.Expression is not TypeOfExpressionSyntax)
                {
                    AddOnce(result, reported, inv, RuleJsonDeserializeDbConnection, ArgumentReason(RuleJsonDeserializeDbConnection), blocked: true);
                    return;
                }
                var target = model.GetTypeInfo(((TypeOfExpressionSyntax)argument.Expression).Type).Type;
                if (target != null && IsOrDerivesFrom(target, DbConnectionType))
                {
                    AddOnce(result, reported, inv, RuleJsonDeserializeDbConnection, ArgumentReason(RuleJsonDeserializeDbConnection), blocked: true);
                    return;
                }
                continue;
            }
            if (staticType != null && IsOrDerivesFrom(staticType, DbConnectionType))
            {
                AddOnce(result, reported, inv, RuleJsonDeserializeDbConnection, ArgumentReason(RuleJsonDeserializeDbConnection), blocked: true);
                return;
            }
        }
    }

    /// <summary>规则码 → 中文说明（与 DescribeRules 同源，规则调整后提示词与拦截信息自动跟随）。</summary>
    private static string ArgumentReason(string code)
    {
        var index = Array.FindIndex(ArgumentRuleNotes, n => n.Code == code);
        return index >= 0 ? ArgumentRuleNotes[index].Description : code;
    }

    /// <summary>实参表内是否存在「只指向内存库」的字符串字面量（变量/拼接/插值一律不算）。</summary>
    private static bool ContainsMemoryOnlyLiteral(ArgumentListSyntax arguments)
    {
        return arguments.Arguments.Any(a => a.Expression is LiteralExpressionSyntax literal
            && IsMemoryOnlyLiteral(literal) && IsMemoryOnlyConnectionString(literal.Token.ValueText));
    }

    private static bool IsMemoryOnlyLiteral(LiteralExpressionSyntax literal)
    {
        return literal.IsKind(SyntaxKind.StringLiteralExpression)
            && IsMemoryOnlyConnectionString(literal.Token.ValueText);
    }

    /// <summary>
    /// 连接串是否「只指向内存库」：逐键解析后要求 Data Source/DataSource/Filename 恰为 :memory:（忽略大小写）
    /// 或 Mode 为 Memory，其它键不影响判定。
    /// 比「包含 :memory: 子串」更严：Data Source=/tmp/x:memory:y.db 这类「路径里带关键字」的形态会落盘，
    /// 不得借子串匹配放行（无键值形态则要求整体恰为 :memory:）。
    /// </summary>
    private static bool IsMemoryOnlyConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }
        var memory = false;
        var sawPair = false;
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            sawPair = true;
            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase)
                || key.Equals("DataSource", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Filename", StringComparison.OrdinalIgnoreCase))
            {
                memory |= value.Equals(":memory:", StringComparison.OrdinalIgnoreCase);
            }
            else if (key.Equals("Mode", StringComparison.OrdinalIgnoreCase))
            {
                memory |= value.Equals("Memory", StringComparison.OrdinalIgnoreCase);
            }
        }
        return sawPair ? memory : connectionString.Trim().Equals(":memory:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>类型全名相等，或沿继承链（BaseType）命中目标类型全名。</summary>
    private static bool IsOrDerivesFrom(ITypeSymbol type, string typeName)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.ToDisplayString() == typeName)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>③ 启发式警告（不阻断）：疑似死循环、硬编码 URL。</summary>
    private static void ScanHeuristics(CompilationUnitSyntax root, SyntaxTree tree, GateResult result)
    {
        var loops = CodeNodes(root).OfType<WhileStatementSyntax>()
            .Where(w => w.Condition is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.TrueLiteralExpression))
            .Select(w => (SyntaxNode)w)
            .Concat(CodeNodes(root).OfType<ForStatementSyntax>()
                .Where(f => f.Condition == null)
                .Select(f => (SyntaxNode)f));

        foreach (var loop in loops)
        {
            var body = loop switch
            {
                WhileStatementSyntax w => (SyntaxNode)w.Statement,
                ForStatementSyntax f => f.Statement,
                _ => null
            };
            if (body != null && !ReferencesCancelToken(body))
            {
                result.Warnings.Add(new Issue
                {
                    Line = LineOf(loop, tree),
                    Code = "dead-loop",
                    Message = "疑似死循环：循环条件/体内未引用取消令牌，建议改为 while(!ct.IsCancellationRequested)，否则 ForceEndTime 到期也无法及时结束"
                });
            }
        }

        // 硬编码 URL：提醒 ctx.Http 语义（代理预配/超时）；平台内部数据不存在回调本机 API 的合法场景。
        // 已作为 ctx.Http 调用实参的 URL 不告警（官方推荐用法本身）。
        var urlWarned = 0;
        var text = tree.ToString();
        foreach (var match in UrlRegex().Matches(text).Cast<Match>())
        {
            if (urlWarned >= 5)
            {
                break;
            }
            var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(match.Index, match.Length), getInnermostNodeForTie: true);
            if (InsideCtxHttpCall(node))
            {
                continue;
            }
            result.Warnings.Add(new Issue
            {
                Line = tree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(match.Index, 1)).StartLinePosition.Line + 1,
                Code = "hardcoded-url",
                Message = $"硬编码外部 URL【{match.Value}】：外部请求请优先使用 ctx.Http（预配代理/超时 100s）；平台内部数据走 ctx.Env/ctx.Notify 门面"
            });
            urlWarned++;
        }
    }

    /// <summary>URL 字面量是否在 ctx.Http.Xxx(...) 调用的实参位置（形如 await ctx.Http.GetAsync(url, ct)）。</summary>
    private static bool InsideCtxHttpCall(SyntaxNode node)
    {
        for (var current = node?.Parent; current != null; current = current.Parent)
        {
            if (current is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Expression: MemberAccessExpressionSyntax receiver }
                } && receiver.Name.Identifier.ValueText == "Http")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>循环体内是否引用了取消令牌（ct/cancellationToken/token 标识符或 IsCancellationRequested）。</summary>
    private static bool ReferencesCancelToken(SyntaxNode body)
    {
        return body.DescendantNodes().OfType<IdentifierNameSyntax>().Any(n =>
                   n.Identifier.ValueText is "ct" or "cancellationToken" or "token")
               || body.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                   .Any(m => m.Name.Identifier.ValueText == "IsCancellationRequested");
    }

    private static void AddBlocked(GateResult result, SyntaxNode node, string code, string reason)
    {
        result.Blocked.Add(new Issue { Line = LineOf(node, node.SyntaxTree), Code = code, Message = reason });
    }

    private static void AddOnce(GateResult result, HashSet<(string, int)> reported, SyntaxNode node, string code, string reason, bool blocked)
    {
        var line = LineOf(node, node.SyntaxTree);
        if (!reported.Add((code, line)))
        {
            return;
        }
        var list = blocked ? result.Blocked : result.Warnings;
        list.Add(new Issue { Line = line, Code = code, Message = reason });
    }

    private static int LineOf(SyntaxNode node, SyntaxTree tree)
    {
        // 取「映射后」行号：编译服务会前置两行 global using + #line 1 预置（见 ScriptBuildService），
        // 物理行号会整体偏移 +2，用户看到的第 N 行必须是脚本自身行号
        return node.GetLocation().GetMappedLineSpan().StartLinePosition.Line + 1;
    }
}
