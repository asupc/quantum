using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Quantum.Plugins;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

namespace Quantum.Application;

/// <summary>
/// 任务脚本保存流水线（计划 2.2）：语法解析 → 安全门禁 → Roslyn 编译 → 按源码 SHA256 缓存产物。
/// 任一环节不过即视为失败（不产 DLL、调用方不入库）；同哈希源码零重编。
/// 产物经 collectible AssemblyLoadContext 加载，源码更新后旧版本 best-effort 卸载。
/// </summary>
public static class ScriptBuildService
{
    /// <summary>任务脚本唯一合法扩展名（保存/上传/执行三侧白名单统一）。</summary>
    public const string ScriptFileExtension = ".cs";

    /// <summary>保存/上传/执行共用的完整流水线结果。</summary>
    public sealed class BuildResult
    {
        public string Hash { get; init; }
        /// <summary>安全门禁拦截项（行号+原因）；非空即失败。</summary>
        public IReadOnlyList<ScriptSecurityGate.Issue> Blocked { get; init; } = [];
        /// <summary>提示类警告（门禁启发式 + 编译警告）；不阻断。</summary>
        public IReadOnlyList<ScriptSecurityGate.Issue> Warnings { get; init; } = [];
        /// <summary>语法/编译错误（行号+消息）。</summary>
        public IReadOnlyList<ScriptSecurityGate.Issue> Errors { get; init; } = [];
        /// <summary>编译产物任务类型全名（成功时非空）。</summary>
        public string TaskTypeName { get; init; }
        public bool Success => Blocked.Count == 0 && Errors.Count == 0;
    }

    /// <summary>脚本保存（PUT /task/scripts）返回结构：三类诊断（blocked/warnings/errors）；Success=false 时内容未落盘。</summary>
    public sealed class ScriptSaveResult
    {
        public bool Success { get; init; }
        public IReadOnlyList<ScriptSecurityGate.Issue> Blocked { get; init; } = [];
        public IReadOnlyList<ScriptSecurityGate.Issue> Warnings { get; init; } = [];
        public IReadOnlyList<ScriptSecurityGate.Issue> Errors { get; init; } = [];
    }

    /// <summary>执行租约：持有一个缓存产段的活跃引用，Dispose 后该产物才允许被卸载淘汰。</summary>
    public sealed class Lease : IDisposable
    {
        private CompiledEntry _entry;
        public BuildResult Result { get; }

        internal Lease(CompiledEntry entry)
        {
            _entry = entry;
            Result = entry.Result;
        }

        /// <summary>每次执行实例化新任务对象（契约要求无共享状态）。</summary>
        public IQuantumTask CreateInstance()
        {
            var type = _entry.GetOrLoadType();
            return (IQuantumTask)Activator.CreateInstance(type)!;
        }

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            entry?.Release();
        }
    }

    /// <summary>单个源码版本的编译产物缓存节点。</summary>
    internal sealed class CompiledEntry
    {
        public string Hash;
        public BuildResult Result;
        /// <summary>原始源码：首载后 AssemblyBytes 置空以省内存，但一旦条目被卸载（LRU/文件更新）
        /// 而仍有持租约者再入，须凭源码重编译恢复产物（见 GetOrLoadType）。</summary>
        public string Source;
        public byte[] AssemblyBytes;
        private TaskAssemblyLoadContext _context;
        private Type _taskType;
        private int _activeCount;
        // 1 = 已从 Cache 摘除但仍有活跃租约：待 activeCount 归零时由 Release 补卸载（§1-11 驱逐统一按 _activeCount）
        private int _stale;

        /// <summary>LRU 驱逐排序用的最近访问时间（TickCount64，Interlocked 读写）。</summary>
        public long LastUseTicks;

        public void Touch() => Interlocked.Exchange(ref LastUseTicks, Environment.TickCount64);

        public int ActiveCount => Volatile.Read(ref _activeCount);

        public Type GetOrLoadType()
        {
            var type = _taskType;
            if (type != null)
            {
                return type;
            }
            lock (this)
            {
                if (_taskType != null)
                {
                    return _taskType;
                }
                if (_context == null)
                {
                    // §1-11 竞态修正：曾成功加载并卸载的条目（Unload 置空 _context/_taskType，首载时又清了 AssemblyBytes），
                    // 若持租约者再入会走到这里——旧实现直接 LoadFromBytes(null) → new MemoryStream(null) 抛。
                    // 凭留存源码重编译补回字节再继续加载。
                    if (AssemblyBytes == null)
                    {
                        var fresh = ComputeEntry(Source, Hash);
                        if (fresh.AssemblyBytes == null)
                        {
                            throw new InvalidOperationException(
                                $"脚本 {Hash[..8]} 产物重载时重编译失败，无法恢复任务类型 {Result.TaskTypeName}");
                        }
                        AssemblyBytes = fresh.AssemblyBytes;
                    }
                    _context = new TaskAssemblyLoadContext(Hash);
                    _context.LoadFromBytes(AssemblyBytes);
                }
                _taskType = _context.FindTaskType(Result.TaskTypeName);
                // ALC 已物化程序集，原始字节不再需要（否则字节与已加载程序集双份常驻）
                AssemblyBytes = null;
                return _taskType;
            }
        }

        public void Acquire() => Interlocked.Increment(ref _activeCount);

        public void Release()
        {
            var remaining = Interlocked.Decrement(ref _activeCount);
            // 被驱逐时仍有活跃租约（Evict 只置 stale 不卸载）：最后一个租约释放时补卸载 collectible ALC
            if (remaining == 0 && Volatile.Read(ref _stale) == 1)
            {
                Unload();
            }
        }

        /// <summary>驱逐入口（已从 Cache 摘除后调用）：无活跃租约即刻卸载，有则标记待卸。</summary>
        public void Evict()
        {
            Volatile.Write(ref _stale, 1);
            if (Volatile.Read(ref _activeCount) == 0)
            {
                Unload();
            }
        }

        /// <summary>best-effort 卸载（活跃执行持有引用时由 CLR 自动延迟到引用释放后）。</summary>
        public void Unload()
        {
            lock (this)
            {
                _taskType = null;
                _context?.Unload();
                _context = null;
            }
        }
    }

    /// <summary>collectible ALC：任务程序集可卸载；未重写的 Load 回退默认上下文解析（BCL/Abstractions 与宿主同一份类型）。</summary>
    private sealed class TaskAssemblyLoadContext : AssemblyLoadContext
    {
        public TaskAssemblyLoadContext(string hash) : base($"quantum-task-{(hash.Length > 8 ? hash[..8] : hash)}", isCollectible: true)
        {
        }

        public void LoadFromBytes(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            LoadFromStream(stream);
        }

        public Type FindTaskType(string typeName)
        {
            var type = Assemblies.SelectMany(a => a.GetTypes()).FirstOrDefault(t => t.FullName == typeName);
            if (type == null)
            {
                throw new InvalidOperationException($"任务程序集中找不到类型 {typeName}");
            }
            return type;
        }
    }

    /// <summary>同哈希只构建一次（并发首次保存/执行共用）。</summary>
    private static readonly ConcurrentDictionary<string, Lazy<CompiledEntry>> Cache = new();

    /// <summary>文件路径 → 最近一次成功版本哈希：源码更新后对旧版本做 best-effort 卸载。</summary>
    private static readonly ConcurrentDictionary<string, string> LatestByFile = new();

    /// <summary>§1-11：编译产物缓存上限（含 collectible ALC）。超出按 LRU 驱逐无活跃租约的条目。internal 可测试时调低。</summary>
    internal static int CacheCapacity = 64;

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(CreateReferences, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    // 任务源码隐式 using：与 SDK ImplicitUsings 对齐（System.IO 在列但门禁 ② 拦截其任何符号引用）。
    // Roslyn 5.0 的 CompilationOptions.Usings 不生效，改走「前缀 global using + #line 复位行号」：
    // 诊断与门禁行号与用户源码一致（prelude 恰好一行）
    private const string ImplicitUsingsPrelude = "global using System;global using System.Collections.Generic;global using System.IO;global using System.Linq;global using System.Net.Http;global using System.Threading;global using System.Threading.Tasks;\n#line 1 \"task\"\n";

    private static readonly CSharpCompilationOptions CompilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug)
        .WithNullableContextOptions(NullableContextOptions.Disable)
        // CS1701（程序集版本统一假设，如 Newtonsoft 引用旧版 System.Linq.Expressions）：
        // SDK 工程默认关闭的提示级噪音，任务编译同样抑制，避免每次保存都弹无意义警告
        .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["CS1701"] = ReportDiagnostic.Suppress });

    /// <summary>
    /// 完整流水线（保存/上传/执行侧共用）。同哈希源码命中缓存零开销；
    /// fileName 用于「源码变更后卸载旧版本」的文件维度追踪（可空）。
    /// </summary>
    public static BuildResult Build(string source, string fileName = null)
    {
        var hash = Sha256(source);
        var lazy = Cache.GetOrAdd(hash, _ => new Lazy<CompiledEntry>(() => ComputeEntry(source, hash)));
        var entry = lazy.Value;
        if (!entry.Result.Success)
        {
            // 失败版本不入缓存：门禁/编译诊断对象会随编辑期「保存-失败-重试」逐版累积永驻条目
            //（TryRemove(KeyValuePair) 只移除本次拿到的实例，并发场景无害）
            Cache.TryRemove(new KeyValuePair<string, Lazy<CompiledEntry>>(hash, lazy));
        }
        entry.Touch();

        if (fileName != null && entry.Result.Success)
        {
            var previous = LatestByFile.GetOrAdd(fileName, hash);
            if (previous != hash)
            {
                // 源码更新产生新版本：旧版本不再被文件引用，驱逐（活跃租约持有则延迟到释放后卸载）
                EvictHash(previous);
                LatestByFile[fileName] = hash;
            }
        }
        TrimToCapacity();
        return entry.Result;
    }

    /// <summary>从 Cache 摘除某哈希条目并驱逐（无活跃租约即刻卸载 collectible ALC，有则标记待卸）。</summary>
    private static void EvictHash(string hash)
    {
        if (Cache.TryRemove(hash, out var lazy) && lazy.IsValueCreated)
        {
            lazy.Value.Evict();
        }
    }

    /// <summary>
    /// §1-11 LRU 驱逐：编译产物（含 collectible ALC）随脚本改写无界累积是慢性内存泄漏。
    /// 超容量时按最近访问时间升序摘除「无活跃租约」的条目至容量内；全部活跃则本轮不驱逐（绝不强卸持租约者）。
    /// </summary>
    private static void TrimToCapacity()
    {
        if (Cache.Count <= CacheCapacity)
        {
            return;
        }
        var evictable = new List<(string hash, long ticks)>();
        foreach (var kv in Cache)
        {
            if (kv.Value.IsValueCreated && kv.Value.Value.ActiveCount == 0)
            {
                evictable.Add((kv.Key, kv.Value.Value.LastUseTicks));
            }
        }
        var excess = Cache.Count - CacheCapacity;
        foreach (var (hash, _) in evictable.OrderBy(e => e.ticks).Take(excess))
        {
            EvictHash(hash);
        }
    }

    /// <summary>执行侧入口：拿到租约后 CreateInstance 执行，用完 Dispose（产物才可被卸载）。</summary>
    public static Lease Acquire(string source, string fileName = null)
    {
        var hash = Sha256(source);
        var lazy = Cache.GetOrAdd(hash, _ => new Lazy<CompiledEntry>(() => ComputeEntry(source, hash)));
        var entry = lazy.Value;
        if (!entry.Result.Success)
        {
            // 与 Build 对齐：失败版本不占缓存（否则会带着诊断对象永驻）
            Cache.TryRemove(new KeyValuePair<string, Lazy<CompiledEntry>>(hash, lazy));
        }
        entry.Touch();
        entry.Acquire();
        TrimToCapacity();
        return new Lease(entry);
    }

    /// <summary>
    /// 删除脚本后的缓存清理（TaskService.DeleteScript 调用）：驱逐该文件最近成功版本
    ///（连同产物字节与已加载 ALC）并清 LatestByFile。失败版本不入缓存（Build/Acquire 内即弃），无需按哈希反查。
    /// </summary>
    public static void Remove(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !LatestByFile.TryRemove(fileName, out var hash))
        {
            return;
        }
        EvictHash(hash);
    }

    /// <summary>构造与生产一致的 Compilation（同引用集，「扫的就是编的」）；亦供诊断工具复用。</summary>
    public static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(ImplicitUsingsPrelude + (source ?? string.Empty), ParseOptions);
        return CSharpCompilation.Create("QuantumTask_GateTest", new[] { tree }, References.Value, CompilationOptions);
    }

    private static CompiledEntry ComputeEntry(string source, string hash)
    {
        var tree = CSharpSyntaxTree.ParseText(ImplicitUsingsPrelude + (source ?? string.Empty), ParseOptions);
        var root = tree.GetRoot();

        // 语法错误直接作为 errors 返回（无法建可靠语义模型，也不必再跑门禁/编译）
        var syntaxErrors = root.DescendantNodesAndSelf()
            .SelectMany(n => n.GetDiagnostics())
            .Concat(tree.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => ToIssue(d, tree))
            .ToList();
        if (syntaxErrors.Count > 0)
        {
            return new CompiledEntry { Hash = hash, Result = new BuildResult { Hash = hash, Errors = syntaxErrors } };
        }

        var compilation = CSharpCompilation.Create(
            $"QuantumTask_{hash[..16]}",
            new[] { tree },
            References.Value,
            CompilationOptions);

        // 先门禁、后编译（Emit 仅在门前两关全过时执行——决议 2）
        var gate = ScriptSecurityGate.Scan(tree, compilation);
        if (!gate.Passed)
        {
            return new CompiledEntry
            {
                Hash = hash,
                Result = new BuildResult { Hash = hash, Blocked = gate.Blocked, Warnings = gate.Warnings }
            };
        }

        // 契约预检（编译期解析 IQuantumTask 实现类，比反射更早给出友好诊断）
        var taskType = ResolveTaskType(tree, compilation);
        if (taskType.Error != null)
        {
            return new CompiledEntry { Hash = hash, Result = new BuildResult { Hash = hash, Errors = [taskType.Error] } };
        }

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        var errors = emitResult.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => ToIssue(d, tree)).ToList();
        if (errors.Count > 0 || !emitResult.Success)
        {
            return new CompiledEntry { Hash = hash, Result = new BuildResult { Hash = hash, Errors = errors } };
        }

        var warnings = emitResult.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Warning)
            .Take(20)
            .Select(d => ToIssue(d, tree)).ToList();
        warnings.AddRange(gate.Warnings);

        return new CompiledEntry
        {
            Hash = hash,
            Source = source,
            AssemblyBytes = peStream.ToArray(),
            Result = new BuildResult
            {
                Hash = hash,
                TaskTypeName = taskType.TypeName,
                Warnings = warnings
            }
        };
    }

    /// <summary>编译期解析任务入口类型：必须有且仅有一个公开、含公共无参构造的顶层 IQuantumTask 实现。</summary>
    private static (string TypeName, ScriptSecurityGate.Issue Error) ResolveTaskType(SyntaxTree tree, CSharpCompilation compilation)
    {
        var model = compilation.GetSemanticModel(tree);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        var implementers = new List<INamedTypeSymbol>();
        foreach (var decl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            // 仅顶层类型（命名空间直下）——嵌套类型反射实例化路径复杂，直接拒绝
            if (decl.Parent is not (NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax or CompilationUnitSyntax))
            {
                continue;
            }
            if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol symbol
                || symbol.TypeKind != TypeKind.Class
                || !symbol.AllInterfaces.Any(i => i.ToDisplayString() == "Quantum.Plugins.IQuantumTask"))
            {
                continue;
            }
            implementers.Add(symbol);
        }

        if (implementers.Count == 0)
        {
            return (null, new ScriptSecurityGate.Issue { Line = 1, Code = "no-task", Message = "未找到 IQuantumTask 实现类：任务源码需包含一个 public class 并实现 Quantum.Plugins.IQuantumTask（需公共无参构造）" });
        }
        if (implementers.Count > 1)
        {
            return (null, new ScriptSecurityGate.Issue { Line = 1, Code = "multi-task", Message = $"发现 {implementers.Count} 个 IQuantumTask 实现类（{string.Join("、", implementers.Select(t => t.ToDisplayString()))}），每个任务脚本仅允许一个" });
        }
        var impl = implementers[0];
        if (impl.DeclaredAccessibility != Accessibility.Public)
        {
            return (null, new ScriptSecurityGate.Issue { Line = 1, Code = "task-not-public", Message = $"IQuantumTask 实现类 {impl.ToDisplayString()} 必须为 public" });
        }
        if (!impl.InstanceConstructors.Any(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 0))
        {
            return (null, new ScriptSecurityGate.Issue { Line = 1, Code = "task-no-ctor", Message = $"IQuantumTask 实现类 {impl.ToDisplayString()} 缺少公共无参构造函数" });
        }
        return (impl.ToDisplayString(), null);
    }

    /// <summary>
    /// 编译引用集（计划 2.5）：运行时框架目录全量 BCL + 应用目录平台白名单包。
    /// 白名单外的新增引用需走依赖决议，不要随手加。
    /// </summary>
    private static ImmutableArray<MetadataReference> CreateReferences()
    {
        var list = new List<MetadataReference>();

        // BCL：当前进程共享框架目录（Windows/Linux/Docker 一致存在）。
        // 目录里混有原生 dll（coreclr/clrgc/mscorrc 等）：AssemblyName.GetAssemblyName 对非托管映像
        // 抛 BadImageFormatException，借此预校验跳过——否则错误会在任务编译期炸出 CS0009
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        foreach (var dll in Directory.GetFiles(frameworkDir!, "*.dll"))
        {
            try
            {
                _ = System.Reflection.AssemblyName.GetAssemblyName(dll);
                list.Add(MetadataReference.CreateFromFile(dll));
            }
            catch (Exception)
            {
                // 原生/非托管 dll 跳过（引用面由其余托管程序集覆盖）
            }
        }

        // 平台已有包白名单（自应用目录解析；v3.1：RestSharp/SkiaSharp 已移除）
        var baseDir = AppContext.BaseDirectory;
        string[] whitelist =
        [
            "Quantum.Plugin.Abstractions.dll",
            "Newtonsoft.Json.dll",
            "HtmlAgilityPack.dll",
            "Microsoft.EntityFrameworkCore.dll",
            "Microsoft.EntityFrameworkCore.Relational.dll",
            "Microsoft.EntityFrameworkCore.Sqlite.dll",
            "Microsoft.Data.Sqlite.dll",
        ];
        foreach (var name in whitelist)
        {
            var path = Path.Combine(baseDir, name);
            if (File.Exists(path))
            {
                list.Add(MetadataReference.CreateFromFile(path));
            }
        }
        return list.ToImmutableArray();
    }

    /// <summary>
    /// 脚本可引用的自有/第三方程序集白名单（只读，供 AI Agent 提示词与「平台能力」面板展示）：
    /// 与编译引用集同源；BCL 由框架目录全量提供（仅受门禁命名空间黑名单约束）。
    /// </summary>
    public static IReadOnlyList<string> DescribeReferenceWhitelist()
    {
        return
        [
            "System.*（.NET 框架程序集全量可用，但受门禁黑名单约束，见规则摘要）",
            "Newtonsoft.Json（JSON 序列化/反序列化：JsonConvert、JObject、JArray、JToken）",
            "HtmlAgilityPack（HTML 解析：HtmlDocument、HtmlNode）",
            "Microsoft.EntityFrameworkCore / .Relational / .Sqlite / Microsoft.Data.Sqlite（只读查询已有库时可用）",
            "Quantum.Plugins（任务契约本身：IQuantumTask / QuantumTaskContext / 各门面接口）",
            "脚本是单文件编译：不要把辅助类拆到另一个 .cs 文件，也不要用 partial 跨文件"
        ];
    }

    private static ScriptSecurityGate.Issue ToIssue(Diagnostic diagnostic, SyntaxTree tree)
    {
        var line = 1;
        var location = diagnostic.Location;
        if (location?.SourceTree == tree && location.IsInSource)
        {
            // 映射后行号：前景是前置 global using 预置（+2 物理行），诊断须回到脚本自身行号
            line = location.GetMappedLineSpan().StartLinePosition.Line + 1;
        }
        return new ScriptSecurityGate.Issue
        {
            Line = line,
            Code = diagnostic.Id,
            Message = diagnostic.GetMessage()
        };
    }

    private static string Sha256(string source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source ?? string.Empty));
        return Convert.ToHexString(bytes);
    }
}
