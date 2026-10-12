// ============================================================================
// 我的密码（由 quantum_passwod_query.js 转换，2026-09-16）：
//   如「我的密码 量子」→ 查询 Data1 含「量子」的密码；多条时逐条列出。
// 迁移映射（计划 2.7）：sendNotify→ctx.Notify；getCustomData→ctx.CustomData.QueryAsync；
//   process.env→ctx.Variables。查询语义与平台一致（包含匹配）。
// 2026-09-18：用户体系移除后不再按通讯账号过滤（Data4 归属列已无绑定关系，仅保存侧留痕），
//   只按关键词 Data1 查——否则存量行（旧 userid）与新增行（恒为管理员名）都对不上。
// 多条匹配优化：改为编号列表，逐条带生成时间（查询结果按创建时间倒序，序号 1 最新），
//   条目间用空行分隔，避免多条密码连成一片无法辨认。
// 2026-10-01：多条区分强化——每条前加「━━━ 第 N 条 ━━━」分隔线（纯文本，各通道通用，
//   不依赖富文本渲染），第一条标注（最新）；QQ 机器人等纯文本通道也能明显区分条目。
//   同时每次推送前 ctx.Log 落一份推送内容，便于事后核对「推送是否有变化」。
// 2026-10-02：关键词改为忽略大小写匹配——库中存在「agentrouter / (空格)AgentRouter(空格)…」
//   这类大小写/空格不同的重复条目，平台 Data1 过滤是包含匹配且区分大小写，导致
//   QQ 与 App 用不同写法查询时命中不同条目、返回完全不同的密码。
//   现统一取该类型全量（平台已按创建时间倒序）后本地忽略大小写过滤：
//   同名条目无论怎么写都能全部列出，重复即所见即所得。
// ============================================================================
using Quantum.Plugins;

public class QueryPasswordTask : IQuantumTask
{
    private const string CustomDataType = "quantum_password";

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var command = ctx.Variables.TryGetValue("command", out var c) ? c : "";
        command = command.Replace("我的密码", "").Trim();
        if (string.IsNullOrEmpty(command))
        {
            ctx.Log("推送：我的密码 / 该指令格式为：我的密码xxxx");
            await ctx.Notify.SendAsync("我的密码", "该指令格式为：我的密码xxxx", ct);
            return;
        }

        // 忽略大小写匹配：平台 Data1 过滤为包含匹配且区分大小写（实测「agentrouter」
        // 与「AgentRouter」命中不同行），这里取全量后本地比较，保证各通道结果一致。
        var keyword = command.ToLowerInvariant();
        var all = await ctx.CustomData.QueryAsync(CustomDataType, ct: ct);
        var datas = new List<QuantumCustomDataValue>();
        foreach (var item in all)
        {
            if (!string.IsNullOrEmpty(item.Data1) && item.Data1.ToLowerInvariant().Contains(keyword))
            {
                datas.Add(item);
            }
        }

        if (datas.Count == 0)
        {
            ctx.Log($"推送：我的密码 / 没有找到关键词:{command}的密码。");
            await ctx.Notify.SendAsync("我的密码", $"没有找到关键词:{command}的密码。", ct);
        }
        else if (datas.Count == 1)
        {
            ctx.Log($"推送：我的密码 / {datas[0].Data2}");
            await ctx.Notify.SendAsync("我的密码", datas[0].Data2, ct);
        }
        else
        {
            var message = $"为关键词「{command}」找到 {datas.Count} 条密码（按生成时间倒序，序号 1 最新）：";
            for (var i = 0; i < datas.Count; i++)
            {
                var element = datas[i];
                var remark = string.IsNullOrWhiteSpace(element.Data3) ? "-" : element.Data3;
                var head = i == 0 ? "（最新）" : "";
                message += $"\r\n\r\n━━━━ 第 {i + 1} 条{head} ━━━━\r\n说明：{element.Data1}\r\n密码：{element.Data2}\r\n备注：{remark}\r\n生成时间：{element.CreateTime:yyyy-MM-dd HH:mm}";
            }
            ctx.Log("推送内容：\r\n" + message);
            await ctx.Notify.SendAsync("我的密码", message, ct);
        }
    }
}
