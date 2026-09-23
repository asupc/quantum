// 外部请求示例：ctx.Http 请求第三方站点（预配代理/超时 100s）。
// 平台内部数据（环境变量/通知）不要走 HTTP——用 ctx.Env / ctx.Notify 门面进程内直调。
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class ExternalApiTask : IQuantumTask
{
    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        ctx.Log($"任务「{ctx.TaskName}」开始请求外部 API（EnableProxy={ctx.EnableProxy}）。");
        try
        {
            // ctx.Http 已预配超时 100s；EnableProxy 时自动走系统代理。异步调用务必传 ct。
            using var response = await ctx.Http.GetAsync("https://httpbin.org/get", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            ctx.Log($"HTTP {(int)response.StatusCode}，响应长度 {body.Length} 字符。");
            // Newtonsoft.Json 在任务编译引用集内（也可用 System.Text.Json）
            var json = JObject.Parse(body);
            ctx.Log($"对端识别 User-Agent：{json["headers"]?["User-Agent"]?.ToString() ?? "(空)"}");
        }
        catch (OperationCanceledException)
        {
            ctx.Log("请求被取消（ForceEndTime 到期）。");
            throw;
        }
        catch (HttpRequestException e)
        {
            // 外部站点不可达属正常运维噪音：记日志即可，勿让整个任务崩掉
            ctx.Log($"外部请求失败（站点不可达/网络策略）：{e.Message}");
        }
        ctx.Log("外部请求任务结束。");
    }
}
