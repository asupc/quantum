// ============================================================================
// 网易体脂秤数据（由 网易体脂秤数据.js 转换，2026-09-17）：拉取当月体脂数据，按记录时间去重后写入
// quantum_health 自定义数据，并维护数据管理页表头。
// 后端要求：需含 2026-09-17 的 ctx.CustomData 契约扩展（Data6-Data15 / 过滤器 / UpdateAsync / SaveTitleAsync）；
//   旧构建会以「'QuantumCustomDataValue' does not contain a definition for 'Data6'」拒绝保存，请先重新部署后端。
// 迁移映射（计划 2.7）：api → ctx.Http；getCustomData → ctx.CustomData.QueryAsync（Data11 过滤）；
//   addCustomData → ctx.CustomData.AddAsync；addOrUpdateCustomDataTitle → ctx.CustomData.SaveTitleAsync；
//   moment → DateTimeOffset；JSON.parse → Newtonsoft.Json；console.log → ctx.Log。
// 环境变量（2026-09-19 脱敏改造：设备/用户标识与会话 Cookie 改从环境变量读取，脚本不再内置）：
//   NTES_SCALE_EQUIP_ID（设备Id）/ NTES_SCALE_USER_ID（用户Id）/ NTES_SCALE_COOKIE（整串 Cookie），均必填。
// 与旧脚本的差异（有意为之）：
//   1) 去重的创建时间上界由「今天 00:00」改为「此刻」，否则当天已写入的记录会漏判而被重复插入。
//   2) 数值列统一按不变文化格式化（旧脚本依赖 JS 数字隐式转换，等值）。
//   3) 旧脚本的 JSON.parse(item.data) 未做容错，这里单条解析失败只记日志跳过，不中断整批。
// ============================================================================
using System.Globalization;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class NetEaseScaleTask : IQuantumTask
{
    private const string CustomDataType = "quantum_health";

    private const string BaseUrl = "https://3c.163.com/ai-app/nit-device/equipUser/data/month";

    private static readonly string[] Titles =
    [
        "体重（kg）", "BMI", "体脂率（%）", "骨量（kg）", "皮下脂肪率（%）", "内脏脂肪等级",
        "肌肉量（kg）", "蛋白质率（%）", "基础代谢（kcal）", "身体年龄（岁）", "记录时间", "数据ID"
    ];

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var equipId = Var(ctx, "NTES_SCALE_EQUIP_ID") ?? throw new Exception("缺少环境变量 NTES_SCALE_EQUIP_ID，请先在环境变量页配置");
        var userId = Var(ctx, "NTES_SCALE_USER_ID") ?? throw new Exception("缺少环境变量 NTES_SCALE_USER_ID，请先在环境变量页配置");
        var cookie = Var(ctx, "NTES_SCALE_COOKIE") ?? throw new Exception("缺少环境变量 NTES_SCALE_COOKIE，请先在环境变量页配置");
        var month = DateTime.Now.ToString("yyyy-MM");
        var now = DateTime.Now;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var list = await GetHealthDataAsync(ctx, equipId.Trim(), userId.Trim(), cookie.Trim(), month, ct);
        if (list == null)
        {
            return;
        }

        var datas = new List<QuantumCustomDataValue>();
        foreach (var item in list)
        {
            ct.ThrowIfCancellationRequested();
            var recordId = item["recordId"]?.ToString();
            var date = FormatDate(item["createTime"]);
            if (string.IsNullOrEmpty(date))
            {
                ctx.Log($"记录 {recordId} 缺少 createTime，跳过。");
                continue;
            }

            JObject detail;
            try
            {
                detail = JObject.Parse(item["data"]?.ToString() ?? "{}");
            }
            catch (Exception e)
            {
                ctx.Log($"记录 {recordId} 的 data 解析失败（{e.Message}），跳过。");
                continue;
            }

            // 去重：Data11 命中当天即视为已写入（旧脚本「[date]数据已存在。」）
            var exists = await ctx.CustomData.QueryAsync(CustomDataType, new QuantumCustomDataFilter
            {
                Data11 = date,
                CreateTimeStart = monthStart,
                CreateTimeEnd = now
            }, ct);
            if (exists.Count > 0 || datas.Any(n => n.Data11 == date))
            {
                ctx.Log($"数据已存在。\n{date}当日重复数据，跳过");
                continue;
            }

            var weight = Num(detail["weight"]);
            var muscle = Num(detail["muscle"]);
            var customData = new QuantumCustomDataValue
            {
                Type = CustomDataType,
                Data1 = Plain(weight / 1000d),
                Data2 = Fixed2(Num(detail["bmi"])),
                Data3 = Plain(Num(detail["bfr"])),
                Data4 = Plain(Num(detail["bone"])),
                Data5 = Plain(Num(detail["subcutfat"])),
                Data6 = Plain(Num(detail["vfal"])),
                Data7 = Fixed2(muscle * weight / 100000d),
                Data8 = Plain(Num(detail["protein"])),
                Data9 = Plain(Num(detail["bmr"])),
                Data10 = Plain(Num(detail["age"])),
                Data11 = date,
                Data12 = recordId
            };
            ctx.Log($"待写入：{Newtonsoft.Json.JsonConvert.SerializeObject(customData)}");
            datas.Add(customData);
        }

        if (datas.Count > 0)
        {
            await ctx.CustomData.AddAsync(datas, ct);
            ctx.Log($"已写入 {datas.Count} 条体脂数据。");
        }
        else
        {
            ctx.Log("本月无新增体脂数据。");
        }

        await ctx.CustomData.SaveTitleAsync(CustomDataType, "健康数据", Titles, ct);
    }

    private static async Task<JArray> GetHealthDataAsync(QuantumTaskContext ctx, string equipId, string userId, string cookie, string month, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{BaseUrl}?equipId={equipId}&userId={userId}&month={month}");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await ctx.Http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                ctx.Log($"体脂数据请求失败：HTTP {(int)response.StatusCode} {body}");
                return null;
            }
            ctx.Log(body);
            return JObject.Parse(body)["result"]?["list"] as JArray;
        }
        catch (Exception e)
        {
            ctx.Log($"体脂数据请求异常：{e.Message}");
            return null;
        }
    }

    /// <summary>moment(item.createTime).format("YYYY-MM-DD HH")：偏移串按偏移换算到本机时区，无偏移串按本机时区。</summary>
    private static string FormatDate(JToken token)
    {
        var raw = token?.ToString();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime().ToString("yyyy-MM-dd HH");
        }
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH")
            : null;
    }

    private static double? Num(JToken token)
    {
        if (token == null || token.Type is JTokenType.Null or JTokenType.Undefined)
        {
            return null;
        }
        return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>JS 数字隐式转字符串：最短往返表示（如 70.5 → 70.5）。</summary>
    private static string Plain(double? value) => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>JS Number.prototype.toFixed(2)。</summary>
    private static string Fixed2(double? value) => value?.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>取环境变量值，空白视为未配置。</summary>
    private static string Var(QuantumTaskContext ctx, string name)
        => ctx.Variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

