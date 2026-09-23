using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Quantum.Application;

/// <summary>
/// 「AI 已知的平台能力」摘要（2026-09-20 新增，AI 脚本修复 Agent 计划阶段三）：
/// 反射 Quantum.Plugin.Abstractions 生成契约签名（并拼上同名 .xml 里的中文注释），
/// 再附上门禁规则、可引用程序集、脚本编写约定与运行时事实——全部由代码生成，零手工维护，
/// 契约/门禁一改，喂给模型的摘要自动跟随（不会出现提示词与实现漂移）。
/// </summary>
public static class ScriptContractCatalog
{
    private static readonly Lazy<string> Cached = new(Build, isThreadSafe: true);

    /// <summary>平台能力摘要（首次生成后缓存）。</summary>
    public static string Describe() => Cached.Value;

    /// <summary>仅契约部分（类型与方法签名），供测试断言与页面预览。</summary>
    public static string DescribeContract()
    {
        var sb = new StringBuilder();
        var assembly = typeof(Quantum.Plugins.IQuantumTask).Assembly;
        var docs = LoadXmlDocs(assembly);
        foreach (var type in assembly.GetExportedTypes().OrderBy(n => n.Name, StringComparer.Ordinal))
        {
            sb.AppendLine($"### {DescribeTypeKind(type)} {type.Name}");
            var typeDoc = docs.GetValueOrDefault($"T:{type.FullName}");
            if (!string.IsNullOrWhiteSpace(typeDoc))
            {
                sb.AppendLine($"// {typeDoc}");
            }
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(n => n.DeclaringType == type))
            {
                var doc = docs.GetValueOrDefault($"P:{type.FullName}.{property.Name}");
                sb.AppendLine($"  {ShortType(property.PropertyType)} {property.Name}{(string.IsNullOrWhiteSpace(doc) ? "" : $"  // {doc}")}");
            }
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(n => n.DeclaringType == type && !n.IsSpecialName))
            {
                var parameters = string.Join(", ", method.GetParameters().Select(FormatParameter));
                var doc = docs.GetValueOrDefault($"M:{type.FullName}.{method.Name}");
                sb.AppendLine($"  {ShortType(method.ReturnType)} {method.Name}({parameters}){(string.IsNullOrWhiteSpace(doc) ? "" : $"  // {doc}")}");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string Build()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 平台能力契约（脚本可用的全部内置方法，签名由程序集反射生成）");
        sb.AppendLine(DescribeContract());
        sb.AppendLine();
        sb.AppendLine("## 脚本编写约定");
        sb.AppendLine("- 脚本是**单文件**：实现 Quantum.Plugins.IQuantumTask，编译时只有本文件 + 下列可引用程序集；");
        sb.AppendLine("  不要把辅助类型拆到别的文件，也不要用 partial 跨文件。");
        sb.AppendLine("- 入口方法：`public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)`；实现类必须 public、有无参构造。");
        sb.AppendLine("- 长循环请定期检查 `ct`（推荐 `while (!ct.IsCancellationRequested)`）；平台用协作取消，不能强杀线程。");
        sb.AppendLine("- 输出一律走 `ctx.Log(...)`（实时日志 + 落盘），不要用 Console。");
        sb.AppendLine("- 环境变量读取：`ctx.Variables` 字典（平台已按任务全量注入）或 `ctx.Env.QueryAsync(name)`。");
        sb.AppendLine("  变量名规则 `^[a-zA-Z][a-zA-Z0-9_]{1,64}$`；多账号用**同名多条**变量，平台以 `&` 连接投递，脚本自行 `Split('&')`。");
        sb.AppendLine("- **禁止硬编码**密钥/Cookie/内网地址/账号：一律读环境变量，缺失时抛异常提示「缺少环境变量 X」。");
        sb.AppendLine("- 通知用户走 `ctx.Notify.*`（文本/图片/视频/音频/选项），是否推送由 `ctx.EnablePush` 决定。");
        sb.AppendLine("- 平台内部数据不要走 `ctx.Http`（那是给外部第三方站点用的），环境变量/通知/自定义数据都有直调门面。");
        sb.AppendLine("- 注释与面向用户的文案用中文。");
        sb.AppendLine();
        sb.AppendLine("## 安全门禁（保存/编译/试运行都会强制复查，不通过即拒绝落盘）");
        sb.AppendLine(string.Join(Environment.NewLine, ScriptSecurityGate.DescribeRules()));
        sb.AppendLine();
        sb.AppendLine("## 可引用的程序集");
        sb.AppendLine(string.Join(Environment.NewLine, ScriptBuildService.DescribeReferenceWhitelist().Select(n => "- " + n)));
        sb.AppendLine();
        sb.AppendLine("## 运行时事实");
        sb.AppendLine("- 脚本存放根目录：`./scripts/quantum`；任务的 FileName 就是该根下的相对路径（如 `B站任务.cs`、`open-trigger-task/x.cs`）。");
        sb.AppendLine("- 执行日志：`./logs/{脚本名首段}/yyyyMMddHHmmssfff.log`，按次落盘（内容含脚本 ctx.Log 输出与异常堆栈）。");
        sb.AppendLine("- 任务可配置：触发指令/定时 Cron/会话名（通知落哪个 App 会话）/是否推送；环境变量是**全局**注入的（不是按任务绑定）。");
        return sb.ToString().TrimEnd();
    }

    private static string DescribeTypeKind(Type type)
        => type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : "class";

    private static string FormatParameter(ParameterInfo parameter)
    {
        var text = $"{ShortType(parameter.ParameterType)} {parameter.Name}";
        if (parameter.HasDefaultValue)
        {
            text += $" = {FormatDefault(parameter.DefaultValue)}";
        }
        return text;
    }

    private static string FormatDefault(object value)
        => value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            bool b => b ? "true" : "false",
            _ => value.ToString()
        };

    /// <summary>类型短名（去命名空间，泛型递归展开；可空标注去掉噪音）。</summary>
    private static string ShortType(Type type)
    {
        if (type.IsGenericType)
        {
            var name = type.Name[..type.Name.IndexOf('`')];
            var args = string.Join(", ", type.GetGenericArguments().Select(ShortType));
            return $"{name}<{args}>";
        }
        if (type.IsArray)
        {
            return ShortType(type.GetElementType()) + "[]";
        }
        return type.Name;
    }

    /// <summary>读取契约程序集旁的同名 .xml（生成文档未开启时返回空表，摘要退化为纯签名）。</summary>
    private static Dictionary<string, string> LoadXmlDocs(Assembly assembly)
    {
        var docs = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var xmlPath = Path.ChangeExtension(assembly.Location, ".xml");
            if (!File.Exists(xmlPath))
            {
                return docs;
            }
            foreach (var member in XDocument.Load(xmlPath).Descendants("member"))
            {
                var name = member.Attribute("name")?.Value;
                var summary = member.Element("summary")?.Value;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(summary))
                {
                    docs[name] = string.Join(' ', summary.Split((char[])['\r', '\n', '\t'],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
        }
        catch
        {
            // 摘要缺失不致命：退化为纯签名即可（Agent 仍能用，只是少一层说明）
        }
        return docs;
    }
}
