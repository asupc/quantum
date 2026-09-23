// ============================================================================
// 随机舞蹈视频：调用 nonebot API 聚合站的随机舞蹈视频接口，把返回的 mp4 直链
// 以视频气泡推送（App 会话内点按即播）。接口为公网第三方公开免费端点
// （任务固有地址，按脚本脱敏规范可保留常量），无需凭据、无环境变量。
// 行为约定：接口 HTTP 非 2xx 或 code!=200 / data 为空 → 推失败文本通知（含接口 message）；
//   成功 → SendVideo 气泡（配文固定，无封面可推）。
// ============================================================================
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class DanceVideoTask : IQuantumTask
{
    private const string ApiUrl = "http://api.nonebot.top/api/v1/random/dance_video";

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            using var response = await ctx.Http.GetAsync(ApiUrl, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            ctx.Log("接口响应: " + body);

            if (!response.IsSuccessStatusCode)
            {
                await ctx.Notify.SendAsync("随机舞蹈视频", $"接口请求失败：HTTP {(int)response.StatusCode}", ct);
                return;
            }

            JObject result;
            try
            {
                result = string.IsNullOrWhiteSpace(body) ? null : JObject.Parse(body);
            }
            catch (Exception e)
            {
                ctx.Log("响应不是合法 JSON: " + e.Message);
                result = null;
            }

            var videoUrl = result?["data"]?.ToString();
            if (result == null || result.Value<int?>("code") != 200 || string.IsNullOrWhiteSpace(videoUrl))
            {
                var msg = result?["message"]?.ToString();
                await ctx.Notify.SendAsync("随机舞蹈视频",
                    "未获取到视频地址：" + (string.IsNullOrEmpty(msg) ? "接口返回 code/data 异常" : msg), ct);
                return;
            }

            await ctx.Notify.SendVideoAsync(videoUrl, "随机舞蹈视频，点按播放", ct: ct);
            ctx.Log("已推送视频: " + videoUrl);
        }
        catch (Exception e)
        {
            ctx.Log("执行异常: " + e);
            await ctx.Notify.SendAsync("随机舞蹈视频", "执行异常：" + e.Message, ct);
        }
    }
}
