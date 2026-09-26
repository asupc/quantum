using System.Collections.Concurrent;
using Quantum.Plugins;
using Quantum.Utils;
using System.Text;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;

namespace Quantum.Application;

/// <summary>
/// 任务执行引擎（2026-09-16 改造：C# 源码任务进程内执行，替代原 node/python 子进程方案）。
/// 保存流水线（门禁+编译+哈希缓存）见 <see cref="ScriptBuildService"/>；
/// 平台能力门面（ctx.Env/ctx.Notify 进程内直调）见 <see cref="TaskPluginHost"/>。
/// </summary>
public static class TaskExcuteService
{
    /// <summary>
    /// 执行中任务的实时日志（键 = 日志相对路径）：值为 <see cref="LiveLogBuffer"/>——
    /// 有界环形缓冲（只驻留最近 N 行供实时端点廉价取尾）+ 逐行增量落盘（完整日志持久化于文件）。
    /// §1-9：旧实现用无界 StringBuilder 常驻整段日志、结束时再 ToString()+Trim 整串双拷贝（大日志进 LOH），
    /// 长跑高频任务内存与分配均失控。读取端（LogsService.GetDetailsAsync）TryGetValue 命中即调 Snapshot()（内建锁快照）。
    /// </summary>
    public static ConcurrentDictionary<string, LiveLogBuffer> Logs { get; set; } = new();

    /// <summary>
    /// 实时日志缓冲（§1-9）：对外只暴露线程安全的 AppendLine/Snapshot，内部单锁串行化
    /// （实时回调与取消信号回调可能并发写）。环形缓冲保留最近 <paramref name="capacityLines"/> 行供实时尾览，
    /// 全部行增量写入磁盘文件（每 flushEveryLines 行或 flushInterval 到时 flush，Dispose 补尾），
    /// 使长跑任务内存有界、且崩溃前已落盘。
    /// </summary>
    public sealed class LiveLogBuffer : IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<string> _ring;
        private readonly int _capacity;
        private readonly StreamWriter _writer;
        private readonly int _flushEveryLines;
        private readonly long _flushEveryMs;
        private int _linesSinceFlush;
        private long _lastFlushTicks;

        public LiveLogBuffer(string logPath, int capacityLines = 2000,
            int flushEveryLines = 200, int flushEveryMs = 2000)
        {
            _ring = new Queue<string>();
            _capacity = Math.Max(1, capacityLines);
            // append + FileShare.Read：执行期实时端点/收尾 footer 可并发读，写侧不阻塞读
            _writer = new StreamWriter(logPath, append: true) { AutoFlush = false };
            _flushEveryLines = flushEveryLines;
            _flushEveryMs = flushEveryMs;
            _lastFlushTicks = Environment.TickCount64;
        }

        public void AppendLine(string line)
        {
            lock (_sync)
            {
                _ring.Enqueue(line);
                while (_ring.Count > _capacity)
                {
                    _ring.Dequeue();
                }
                _writer.WriteLine(line);
                if (++_linesSinceFlush >= _flushEveryLines
                    || Environment.TickCount64 - _lastFlushTicks >= _flushEveryMs)
                {
                    _writer.Flush();
                    _linesSinceFlush = 0;
                    _lastFlushTicks = Environment.TickCount64;
                }
            }
        }

        /// <summary>实时尾览快照（环形缓冲最近 N 行按行连接）。</summary>
        public string Snapshot()
        {
            lock (_sync)
            {
                return string.Join(Environment.NewLine, _ring);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _writer.Flush();
                _writer.Dispose();
            }
        }
    }

    /// <summary>
    /// 日志目录名统一算法：脚本名首段剔除路径分隔符。
    /// 写入侧（Run）与记录侧（TaskService/MessageProcess/OpenTriggerTaskService 落 t_log 的 DirectoryName）
    /// 必须同用本方法——否则子目录脚本的日志会写到 A 目录、记录却指向 B 目录，详情永远读不到。
    /// </summary>
    public static string LogDirNameFrom(string fileName)
    {
        return (fileName ?? string.Empty).Split(".")[0].Replace('/', '_').Replace('\\', '_');
    }

    /// <summary>
    /// 出站会话键解析（§2.2）：任务配置了会话名（非空白）→ 会话名 Trim；否则 → 任务 Id（原行为，1 任务 = 1 会话）。
    /// 全仓库唯一注入点在 ExecTask 组装 ctx 处（外部触发/定时/手动执行等一切路径均经此），
    /// 入站路由（MessageProcess.SelectTaskCandidates）与改名迁移（TaskService.UpdateAsync）同用本方法对齐口径。
    /// </summary>
    internal static string ResolveSessionKey(TaskModel task)
        => ResolveSessionKey(task.Id, task.SessionName);

    /// <summary>出站会话键解析（Id/会话名分量版，供匿名投影等无实体场景共用同一口径）。</summary>
    internal static string ResolveSessionKey(string taskId, string sessionName)
        => string.IsNullOrWhiteSpace(sessionName) ? taskId : sessionName.Trim();

    #region 执行并发闸（§1-2）

    /// <summary>
    /// 进程内脚本执行并发闸：TaskExcuteService 是 static class，SemaphoreSlim 必须静态持有
    /// （写成实例字段形同无效）。首次用到时按 Setting.MaxConcurrentTasks 建闸（改值需重启生效）。
    /// </summary>
    private static SemaphoreSlim _executionGate;
    private static readonly object _gateInitLock = new();

    /// <summary>等票超时上限：超时记日志后放行执行（宁可短暂超限，不可无声卡死）——§1-2 主兜底。测试可临时调小后复原。</summary>
    internal static TimeSpan ExecutionSlotWaitTimeout = TimeSpan.FromSeconds(120);

    /// <summary>测试专用：以指定容量重建执行闸（绕开懒初始化）。</summary>
    internal static void SetGateCapacityForTest(int capacity)
    {
        lock (_gateInitLock)
        {
            _executionGate = new SemaphoreSlim(capacity);
        }
    }

    /// <summary>测试专用：清空闸实例，令下次按 Setting 重建。</summary>
    internal static void ResetGateForTest()
    {
        lock (_gateInitLock)
        {
            _executionGate = null;
        }
    }

    private static SemaphoreSlim GetExecutionGate()
    {
        var gate = _executionGate;
        if (gate != null)
        {
            return gate;
        }
        lock (_gateInitLock)
        {
            gate = _executionGate;
            if (gate == null)
            {
                gate = new SemaphoreSlim(Math.Max(1, SystemConfigHelper.GetSetting().MaxConcurrentTasks));
                _executionGate = gate;
            }
            return gate;
        }
    }

    /// <summary>
    /// 申请一个执行槽位：正常拿到返回可释放句柄；等票超时记一条告警并返回不占额度的直通句柄
    /// （超限排队等待而非拒绝，超时兜底放行避免无声卡死）。
    /// </summary>
    internal static async Task<IDisposable> AcquireExecutionSlotAsync(Action<string> logWarn)
    {
        var gate = GetExecutionGate();
        using var timeoutCts = new CancellationTokenSource(ExecutionSlotWaitTimeout);
        try
        {
            await gate.WaitAsync(timeoutCts.Token);
            return new ExecutionSlotReleaser(gate);
        }
        catch (OperationCanceledException)
        {
            logWarn?.Invoke($"执行并发已达上限，等待执行槽位超过 {ExecutionSlotWaitTimeout.TotalSeconds:F0}s 仍未获取，直接放行执行（可能短暂超限）。");
            return NoopSlot.Instance;
        }
    }

    private sealed class ExecutionSlotReleaser : IDisposable
    {
        private SemaphoreSlim _gate;
        public ExecutionSlotReleaser(SemaphoreSlim gate) => _gate = gate;
        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }

    private sealed class NoopSlot : IDisposable
    {
        public static readonly NoopSlot Instance = new();
        public void Dispose() { }
    }

    #endregion

    /// <summary>
    /// 执行脚本任务
    /// </summary>
    /// <param name="taskCommand"></param>
    /// <returns></returns>
    public static async Task Run(this TaskCommandStep taskCommand, CancellationToken ct = default)
    {
        taskCommand.Envs = taskCommand.Envs.Where(n => !string.IsNullOrEmpty(n.Name) && !string.IsNullOrEmpty(n.Value)).ToList();
        // FileName 已限 scripts/quantum 根内，目录名再剔除路径分隔符（纵深防御）
        var dir = $"./logs/{LogDirNameFrom(taskCommand.Task.FileName)}";
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string logPath = $"{dir}/{taskCommand.CreateTime:yyyyMMddHHmmssfff}.log";

        var startTime = DateTime.Now;
        // §1-9：正文由 ExecTask 内的实时缓冲逐行增量落盘，此处只补收尾 footer
        await taskCommand.ExecTask(logPath, ct);
        if (!File.Exists(logPath))
        {
            File.Create(logPath).Close();
        }
        var endTime = DateTime.Now;
        await using (StreamWriter logWriter = new(logPath, true))
        {
            await logWriter.WriteLineAsync($@"任务结束时间：{endTime:yyyy/MM/dd HH:mm:ss.fff}
执行任务用时：{(endTime - startTime).TotalSeconds:F2}秒。");
            await logWriter.FlushAsync();
        }
    }

    /// <summary>
    /// 进程内执行：SafeFile 前置校验 → 门禁/编译（哈希缓存，执行侧对历史脏数据二次校验）
    /// → collectible ALC 加载产物 → 每次执行独立 DI scope 组装 ctx → RunAsync(ctx, ct) 协作取消。
    /// </summary>
    private static async Task ExecTask(this TaskCommandStep taskCommand, string fileName, CancellationToken ct = default)
    {
        await Task.Run(async () =>
        {
            // §1-9：实时缓冲即刻接管——开文件、注册字典、写开始行（逐行增量落盘 + 环形尾览）
            using var buffer = new LiveLogBuffer(fileName);
            Logs[fileName] = buffer;
            buffer.AppendLine($"{taskCommand.Task.Name}任务开始时间：{DateTime.Now:yyyy/MM/dd HH:mm:ss.fff}");

            // 执行路径二次校验（纵深防御）：保存侧 TaskService.ValidateScriptFileName 已挡穿越，
            // 这里对存量数据/导入数据再兜底一次，确保只执行 scripts/quantum 根内的脚本
            var scriptFile = SafeFile.Resolve("./scripts/quantum", taskCommand.Task.FileName);
            if (scriptFile == null)
            {
                buffer.AppendLine($"任务脚本路径非法【{taskCommand.Task.FileName}】，已拒绝执行！");
                return;
            }

            if (!File.Exists(scriptFile))
            {
                buffer.AppendLine($"任务执行脚本【{scriptFile}】不存在！");
                return;
            }

            var ext = Path.GetExtension(scriptFile).ToLowerInvariant();
            if (ext != ScriptBuildService.ScriptFileExtension)
            {
                buffer.AppendLine($"不支持的脚本文件：{scriptFile}");
                buffer.AppendLine($"仅支持 {ScriptBuildService.ScriptFileExtension} 源码任务；.js/.py 旧任务已停止支持，请新建 C# 任务（参考 scripts/quantum 示例）。");
                return;
            }

            string source;
            try
            {
                source = await File.ReadAllTextAsync(scriptFile);
            }
            catch (Exception e)
            {
                buffer.AppendLine($"读取任务脚本失败：{e.Message}");
                return;
            }

            // 执行侧二次校验（计划 2.2）：历史脏数据/绕过 API 手改的源码在执行时重新过保存流水线；
            // 同哈希命中缓存时零编译开销，未命中（如重启后首次执行）现算
            var build = ScriptBuildService.Build(source, scriptFile);
            if (build.Blocked.Count > 0)
            {
                buffer.AppendLine("任务脚本未通过安全门禁，已拒绝执行：");
                foreach (var issue in build.Blocked)
                {
                    buffer.AppendLine($"  [第{issue.Line}行] {issue.Message}");
                }
                return;
            }
            if (build.Errors.Count > 0)
            {
                buffer.AppendLine("任务脚本编译失败，已拒绝执行：");
                foreach (var issue in build.Errors)
                {
                    buffer.AppendLine($"  [第{issue.Line}行] {issue.Message}");
                }
                return;
            }

            // §1-2：占用执行槽位跑脚本（拿到才计数；等票超时则直通放行，避免无声卡死）
            using var slot = await AcquireExecutionSlotAsync(msg => buffer.AppendLine(msg));
            using var lease = ScriptBuildService.Acquire(source, scriptFile);
            IQuantumTask taskInstance;
            try
            {
                taskInstance = lease.CreateInstance();
            }
            catch (Exception e)
            {
                buffer.AppendLine($"任务程序集加载失败：{e.Message}");
                return;
            }

            // 每次执行独立 DI scope（门面直调 scoped 服务），结束即释放；HttpClient 改为按代理开关缓存复用（§1-13），不随执行释放
            await using var scopeDisposer = ScopeDisposer.Capture(TaskPluginHost.CreateScope());
            var provider = scopeDisposer.Scope.ServiceProvider;
            // 实时日志回调与取消信号回调可能并发写：统一串行化（缓冲内建锁）
            void AppendLog(string line) => buffer.AppendLine(line);
            // 文件门面与 ctx.Http 复用同一 HttpClient（代理/超时一致），根目录取自系统配置（缺省 ./downloads）
            var taskHttp = CreateTaskHttpClient(taskCommand.Task.EnableProxy);
            var ctx = new QuantumTaskContext(
                string.IsNullOrEmpty(taskCommand.SubTaskName) ? taskCommand.Task.Name : taskCommand.SubTaskName,
                taskCommand.Task.EnableProxy,
                taskCommand.Task.EnablePush,
                BuildVariables(taskCommand.Envs),
                AppendLog,
                taskHttp,
                new QuantumEnvFacade(provider.GetRequiredService<EnvService>()),
                // 门面持有双键（§2.2）：taskId 供选项载荷根部注入（点选精确路由用真实任务 Id，不受会话名改名影响）；
                // sessionKey = 任务配置的会话名（Trim）‖ 任务 Id，出站消息全部落入该会话（合并归组的关键）
                new QuantumNotifyFacade(provider.GetRequiredService<NotifyService>(), taskCommand.Task.Id,
                    ResolveSessionKey(taskCommand.Task)),
                new QuantumCustomDataFacade(provider.GetRequiredService<CustomDataService>(),
                    provider.GetRequiredService<CustomDataTitleService>()),
                new QuantumFileFacade(SystemConfigHelper.GetSetting().FileDownloadRoot, taskHttp),
                // Docker 门面只放开「重启 + 探活」（证书续期后重载 nginx）；服务延迟解析，不碰 docker 的任务零开销
                new QuantumDockerFacade(() => provider.GetRequiredService<DockerManagementService>()));

            // 链接外部取消令牌（§1-2：TaskJob 把 Quartz 停机 ct 传下来）+ ForceEndTime 定时，二者任一触发即协作取消
            using var forceEndCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (taskCommand.ForceEndTime > DateTime.Now)
            {
                forceEndCts.CancelAfter(taskCommand.ForceEndTime - DateTime.Now);
                // 协作取消语义（计划 2.4）：到期发信号；任务不检查 ct 时线程保留至自行结束/进程重启
                forceEndCts.Token.Register(() => AppendLog(
                    $"任务已达强制结束时间[{taskCommand.ForceEndTime:yyyy/MM/dd HH:mm:ss}]，已发出协作取消信号；若任务未及时响应，其线程将保留直至任务自行结束或进程重启。"));
            }
            else if (taskCommand.ForceEndTime != default && taskCommand.ForceEndTime <= DateTime.Now)
            {
                // 已过期（如编译耗时吃掉超时窗口）：立即取消，否则该任务将永远不会被取消
                buffer.AppendLine($"任务强制结束时间[{taskCommand.ForceEndTime:yyyy/MM/dd HH:mm:ss}]已过期，立即发出协作取消信号。");
                forceEndCts.Cancel();
            }

            try
            {
                await taskInstance.RunAsync(ctx, forceEndCts.Token);
                if (forceEndCts.Token.IsCancellationRequested)
                {
                    buffer.AppendLine(ct.IsCancellationRequested
                        ? "任务收到停机取消信号，已在取消后自行结束。"
                        : $"任务已在取消信号后自行结束（ForceEndTime={taskCommand.ForceEndTime:yyyy/MM/dd HH:mm:ss}）。");
                }
            }
            catch (OperationCanceledException) when (forceEndCts.Token.IsCancellationRequested)
            {
                buffer.AppendLine(ct.IsCancellationRequested
                    ? "任务收到停机取消信号，协作取消生效。"
                    : $"任务已达强制结束时间[{taskCommand.ForceEndTime:yyyy/MM/dd HH:mm:ss}]，协作取消生效。");
            }
            catch (Exception e)
            {
                buffer.AppendLine("task execution warning or exception information：");
                buffer.AppendLine("MessageAsync：" + e.Message);
                buffer.AppendLine("StackTrace：" + e.StackTrace);
            }
            finally
            {
                Logs.TryRemove(fileName, out _);
            }
        });
    }

    /// <summary>任务环境变量合成（与原进程注入同规则：同名以 & 连接，去尾部连接符）。</summary>
    private static IReadOnlyDictionary<string, string> BuildVariables(List<EnvModel> envs)
    {
        return envs
            .Where(n => !string.IsNullOrEmpty(n?.Name))
            .GroupBy(n => n.Name)
            .ToDictionary(g => g.Key, g => string.Join('&', g.Select(n => n.Value)).TrimEnd('&'));
    }

    /// <summary>
    /// 外部请求 HttpClient 缓存（§1-13）：按「代理开关」缓存 handler/client 复用连接池，
    /// 避免每次执行新建 HttpClient/SocketsHttpHandler 造成套接字抖动。缓存项长生命周期，进程退出统一回收，
    /// 执行侧不得 Dispose（见 ExecTask 已移除对 ctx.Http 的释放）。
    /// </summary>
    private static readonly ConcurrentDictionary<bool, HttpClient> _taskHttpClients = new();

    /// <summary>外部请求专用 HttpClient（计划 2.1）：EnableProxy 时走系统代理（容器内 HTTP_PROXY 等），超时 100s。</summary>
    private static HttpClient CreateTaskHttpClient(bool enableProxy)
    {
        return _taskHttpClients.GetOrAdd(enableProxy, proxy =>
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                UseProxy = proxy,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(100)
            };
        });
    }

    /// <summary>IServiceScope 非 IAsyncDisposable，包一层配合 await using 统一释放。</summary>
    private sealed class ScopeDisposer : IAsyncDisposable
    {
        public IServiceScope Scope { get; }
        private ScopeDisposer(IServiceScope scope) => Scope = scope;
        public static ScopeDisposer Capture(IServiceScope scope) => new(scope);
        public ValueTask DisposeAsync()
        {
            Scope.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
