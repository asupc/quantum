namespace Quantum.Plugins;

/// <summary>
/// 任务执行上下文：平台能力的唯一入口。
/// 日志走 <see cref="Log"/>；外部 HTTP 走 <see cref="Http"/>（预配代理/超时）；
/// 平台内部数据（环境变量/通知/自定义数据）走 <see cref="Env"/> / <see cref="Notify"/> / <see cref="CustomData"/> 门面进程内直调。
/// </summary>
public sealed class QuantumTaskContext
{
    private readonly Action<string> _log;

    /// <summary>任务名（多步任务为步骤名）。</summary>
    public string TaskName { get; }

    /// <summary>是否启用代理（原 system_enable_proxy）。</summary>
    public bool EnableProxy { get; }

    /// <summary>是否启用推送（原 system_enable_notify）。</summary>
    public bool EnablePush { get; }

    /// <summary>任务配置的环境变量（已按平台合并规则合成）。</summary>
    public IReadOnlyDictionary<string, string> Variables { get; }

    /// <summary>外部请求专用：预配代理（EnableProxy）/ 超时 100s；平台内部数据不要用它，走 Env/Notify 门面。</summary>
    public HttpClient Http { get; }

    /// <summary>平台环境变量门面（进程内直调 EnvService）。</summary>
    public IQuantumEnv Env { get; }

    /// <summary>平台通知门面（进程内直调 NotifyService）。</summary>
    public IQuantumNotify Notify { get; }

    /// <summary>平台自定义数据门面（进程内直调 CustomDataService）。</summary>
    public IQuantumCustomData CustomData { get; }

    /// <summary>受控文件落盘门面：产物文件下载保存的唯一通道，可写范围限定在下载根目录内。</summary>
    public IQuantumFile File { get; }

    /// <summary>由执行器构造，任务代码只读使用。</summary>
    public QuantumTaskContext(string taskName, bool enableProxy, bool enablePush,
        IReadOnlyDictionary<string, string> variables, Action<string> log, HttpClient http,
        IQuantumEnv env, IQuantumNotify notify, IQuantumCustomData customData,
        IQuantumFile file = null)
    {
        TaskName = taskName;
        EnableProxy = enableProxy;
        EnablePush = enablePush;
        Variables = variables;
        _log = log;
        Http = http;
        Env = env;
        Notify = notify;
        CustomData = customData;
        File = file;
    }

    /// <summary>console.log 等价：实时日志 + 落盘。</summary>
    public void Log(string message) => _log(message ?? string.Empty);
}
