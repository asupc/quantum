// ============================================================================
// 我的密码（由 quantum_passwod_query.js 转换，2026-09-16）：
//   如「我的密码 量子」→ 查询 Data1 含「量子」的密码；多条时逐条列出。
// 迁移映射（计划 2.7）：sendNotify→ctx.Notify；getCustomData→ctx.CustomData.QueryAsync；
//   process.env→ctx.Variables。查询语义与平台一致（包含匹配）。
// 2026-09-18：用户体系移除后不再按通讯账号过滤（Data4 归属列已无绑定关系，仅保存侧留痕），
//   只按关键词 Data1 查——否则存量行（旧 userid）与新增行（恒为管理员名）都对不上。
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
            await ctx.Notify.SendAsync("我的密码", "该指令格式为：我的密码xxxx", ct);
            return;
        }

        var datas = await ctx.CustomData.QueryAsync(CustomDataType, data1: command, ct: ct);
        if (datas.Count == 0)
        {
            await ctx.Notify.SendAsync("我的密码", $"没有找到关键词:{command}的密码。", ct);
        }
        else if (datas.Count == 1)
        {
            await ctx.Notify.SendAsync("我的密码", datas[0].Data2, ct);
        }
        else
        {
            var message = "为您找到多个密码：";
            foreach (var element in datas)
            {
                message += $"\r{element.Data1}备注：{element.Data3}\n{element.Data2}";
            }
            await ctx.Notify.SendAsync("我的密码", message, ct);
        }
    }
}
