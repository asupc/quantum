using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace Quantum.Application;

/// <summary>
/// 任务脚本危险性门禁：三级扫描（计划 2.3）。
/// ① 语法级粗筛：unsafe/fixed/stackalloc/指针/dynamic；
/// ② 语义级黑名单：经 SemanticModel 解析标识符最终绑定符号，按符号所属命名空间/类型判定
///    ——别名、var、using 改名均无法绕过；
/// ③ 启发式警告（不阻断）：疑似死循环（未引用取消令牌）、硬编码 URL。
/// 定位是防误用 + 粗粒度拦截，不是对抗性沙箱；真正的权限边界仍是 ManagerOnly 上传。
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
    };

    // ② 语义黑名单：精确成员（类型全名 + 成员名）
    private static readonly (string TypeName, string Member, string Reason)[] MemberBlacklist =
    {
        ("System.Type", "GetType", "Type.GetType(string) 按名加载类型被禁止：防绕过门禁加载黑名单类型"),
    };

    [GeneratedRegex(@"https?://[^\s""'<>\\)）、，；]+")]
    private static partial Regex UrlRegex();

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
