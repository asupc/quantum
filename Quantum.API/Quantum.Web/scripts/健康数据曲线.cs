// ============================================================================
// 健康数据曲线（由 健康数据曲线.js 转换，2026-09-17）：按指令汇总 quantum_health 某指标的首末变化。
// 后端要求：需含 2026-09-17 的 ctx.CustomData 契约扩展（Data6-Data15，本任务只读该区间的指标列）；
//   旧构建会以「'QuantumCustomDataValue' does not contain a definition for 'Data6'」拒绝保存，请先重新部署后端。
// 迁移映射（计划 2.7）：getCustomData → ctx.CustomData.QueryAsync；sendNotify → ctx.Notify（标题自定）；
//   moment 差值 → DateTime；process.env.command → ctx.Variables["command"]；console.log → ctx.Log。
// 环境变量：command（如「体重」「健康体脂率」，默认「体重」）。
// 与旧脚本的差异（有意为之）：
//   1) 旧脚本读 api/SystemConfig 的 ServerPath 只为拼图表图片地址（chartjs-node-canvas 段已注释停用），
//      图表能力在 C# 侧无对应包，故一并去除，仅保留文字汇总通知。
//   2) 末两条记录同一天（天数为 0）时旧脚本会算出 NaN，这里按 0 处理并记日志。
// 指标列含义与旧 quantum_health 表头一致：Data1 体重 / Data2 BMI / Data3 体脂率 / Data4 骨量 /
//   Data5 皮下脂肪率 / Data6 内脏脂肪等级 / Data7 肌肉量 / Data8 蛋白质率 / Data9 基础代谢 /
//   Data10 身体年龄 / Data11 记录时间。
// ============================================================================
using System.Globalization;
using Quantum.Plugins;

public class HealthChartTask : IQuantumTask
{
    private const string CustomDataType = "quantum_health";

    // 与旧脚本 quantum_health 表头 / typeUnit 一致（无单位的指标 Unit 为空）
    private static readonly (string Key, string Title, string Unit)[] Metrics =
    [
        ("Data1", "体重（kg）", "kg"),
        ("Data2", "BMI", ""),
        ("Data3", "体脂率（%）", "%"),
        ("Data4", "骨量（kg）", "kg"),
        ("Data5", "皮下脂肪率（%）", "%"),
        ("Data6", "内脏脂肪等级", ""),
        ("Data7", "肌肉量（kg）", "kg"),
        ("Data8", "蛋白质率（%）", "%"),
        ("Data9", "基础代谢（kcal）", "kcal"),
        ("Data10", "身体年龄（岁）", "")
    ];

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var command = ctx.Variables.TryGetValue("command", out var c) ? c : null;
        if (string.IsNullOrEmpty(command))
        {
            command = "体重";
        }
        command = command.Replace("健康", "");

        var metric = Metrics.FirstOrDefault(n => n.Title.Contains(command));
        if (metric.Key == null)
        {
            await ctx.Notify.SendAsync("健康数据", $"暂无【{command}】数据。", ct);
            return;
        }

        var rows = await ctx.CustomData.QueryAsync(CustomDataType, ct: ct);
        // 查询结果按创建时间倒序；取「有该指标值」的首末两条（[0] 最早、[^1] 最新）
        var valued = rows.Where(n => !string.IsNullOrEmpty(Get(n, metric.Key))).Reverse().ToList();
        if (valued.Count == 0)
        {
            ctx.Log($"quantum_health 共 {rows.Count} 条记录，均无【{metric.Title}】值。");
            await ctx.Notify.SendAsync("健康数据", $"暂无【{command}】数据。", ct);
            return;
        }

        var first = valued[0];
        var last = valued[^1];
        var firstText = Get(first, metric.Key);
        var lastText = Get(last, metric.Key);
        if (!double.TryParse(firstText, NumberStyles.Float, CultureInfo.InvariantCulture, out var firstValue)
            || !double.TryParse(lastText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lastValue))
        {
            ctx.Log($"指标值无法解析：首 {firstText} / 末 {lastText}");
            await ctx.Notify.SendAsync("健康数据", $"暂无【{command}】数据。", ct);
            return;
        }

        var days = DaysBetween(first.Data11, last.Data11);
        if (days <= 0)
        {
            ctx.Log($"首末记录时间间隔不足 1 天（{first.Data11} → {last.Data11}），平均变化按 0 计。");
        }
        // 与旧脚本同向：v>0 表示从最早到最新是下降
        var delta = firstValue - lastValue;
        var average = days > 0 ? delta / days : 0;
        var direction = delta > 0 ? "下降" : "上升";
        var msg = $"{metric.Title}数据变化\n"
            + $"开始 {first.Data11}：{firstText}{metric.Unit}\n"
            + $"截止 {last.Data11}：{lastText}{metric.Unit}\n"
            + $"累计{days}天，累计{direction}{Math.Abs(delta).ToString("F2", CultureInfo.InvariantCulture)}{metric.Unit}，"
            + $"平均每天{direction}：{Math.Abs(average).ToString("F2", CultureInfo.InvariantCulture)}{metric.Unit}";
        ctx.Log(msg);
        await ctx.Notify.SendAsync("健康数据变化", msg, ct);
    }

    private static string Get(QuantumCustomDataValue row, string key) => key switch
    {
        "Data1" => row.Data1,
        "Data2" => row.Data2,
        "Data3" => row.Data3,
        "Data4" => row.Data4,
        "Data5" => row.Data5,
        "Data6" => row.Data6,
        "Data7" => row.Data7,
        "Data8" => row.Data8,
        "Data9" => row.Data9,
        "Data10" => row.Data10,
        _ => null
    };

    /// <summary>Data11 记录时间形如 2026-09-17 08，返回两时间相差天数（无法解析时 0）。</summary>
    private static int DaysBetween(string start, string end)
    {
        var okStart = DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startTime);
        var okEnd = DateTime.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.None, out var endTime);
        return okStart && okEnd ? (endTime - startTime).Days : 0;
    }
}
