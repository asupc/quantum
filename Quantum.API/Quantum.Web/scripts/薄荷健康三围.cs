// ============================================================================
// 薄荷健康三围（由 薄荷健康三围.js 转换，2026-09-17）：拉取薄荷最近三围记录，按日期去重后写入
// quantum_measurements 自定义数据，并维护数据管理页表头。
// 后端要求：需含 2026-09-17 的 ctx.CustomData 契约扩展（Data6-Data15 / 过滤器 / UpdateAsync / SaveTitleAsync）；
//   旧构建会以「'QuantumCustomDataValue' does not contain a definition for 'Data6'」拒绝保存，请先重新部署后端。
// 迁移映射（计划 2.7）：api → ctx.Http；getCustomData → ctx.CustomData.QueryAsync（Data7 过滤）；
//   addCustomData → ctx.CustomData.AddAsync；addOrUpdateCustomDataTitle → ctx.CustomData.SaveTitleAsync；
//   JSON.parse → Newtonsoft.Json；console.log → ctx.Log。
// 环境变量（2026-09-19 脱敏改造：凭据改从环境变量读取，脚本不再内置）：
//   BOOHEE_TOKEN（接口 token，同时用于 URL 与 Token 头）/ BOOHEE_DEVICE_TOKEN / BOOHEE_ANON_TOKEN /
//   BOOHEE_COOKIE，均必填；过期时改环境变量即可，不必重传脚本。
// 与旧脚本的差异（有意为之）：日志文案「同步到网易数据」改为「待同步三围数据」（旧文案系复制残留）；
//   单条字段缺失按空值写入，不再因个别字段为 undefined 整批失败。
// ============================================================================
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class BooheeMeasurementTask : IQuantumTask
{
    private const string CustomDataType = "quantum_measurements";

    private const string BaseUrl = "https://record.boohee.com/api/v2/measures/recent";

    private static readonly string[] Titles =
    [
        "腰围", "大腿围", "小腿围", "臀围", "胸围", "手臂围", "日期"
    ];

    // 与旧脚本 headers 逐项一致（UserAgent 与 User-Agent 两个键原脚本即同时存在）；
    // 凭据四头（Device-Token/Anonymous-Token/Token/Cookie）已改从环境变量读取，不在此列
    private static readonly (string Name, string Value)[] Headers =
    [
        ("Os-Version", "17.2.1"),
        ("UserAgent", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_2_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148"),
        ("channel", "AppStore"),
        ("App-Device", "ios"),
        ("Phone-Model", "iPhone"),
        ("Utc-Offset", "28800"),
        ("Phone-Platform", "iPhone 12 mini"),
        ("App-Version", "12.4.3"),
        ("Phone-Device", "iPhone13,1"),
        ("App-Key", "one"),
        ("User-Agent", "Apifox/1.0.0 (https://apifox.com)")
    ];

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var token = Var(ctx, "BOOHEE_TOKEN") ?? throw new Exception("缺少环境变量 BOOHEE_TOKEN，请先在环境变量页配置");
        var deviceToken = Var(ctx, "BOOHEE_DEVICE_TOKEN") ?? throw new Exception("缺少环境变量 BOOHEE_DEVICE_TOKEN，请先在环境变量页配置");
        var anonToken = Var(ctx, "BOOHEE_ANON_TOKEN") ?? throw new Exception("缺少环境变量 BOOHEE_ANON_TOKEN，请先在环境变量页配置");
        var cookie = Var(ctx, "BOOHEE_COOKIE") ?? throw new Exception("缺少环境变量 BOOHEE_COOKIE，请先在环境变量页配置");
        var list = await GetDataAsync(ctx, token.Trim(), deviceToken.Trim(), anonToken.Trim(), cookie.Trim(), ct);
        if (list == null)
        {
            return;
        }

        var datas = new List<QuantumCustomDataValue>();
        ctx.Log($"待同步三围数据：【{list.Count}】条");

        foreach (var item in list)
        {
            ct.ThrowIfCancellationRequested();
            var recordOn = Text(item["record_on"]);
            if (string.IsNullOrEmpty(recordOn))
            {
                ctx.Log("记录缺少 record_on，跳过。");
                continue;
            }

            // 去重：Data7（日期）命中即视为已写入
            var exists = await ctx.CustomData.QueryAsync(CustomDataType,
                new QuantumCustomDataFilter { Data7 = recordOn }, ct);
            if (exists.Count > 0 || datas.Any(n => n.Data7 == recordOn))
            {
                ctx.Log($"{recordOn}当日重复数据，跳过");
                continue;
            }

            var customData = new QuantumCustomDataValue
            {
                Type = CustomDataType,
                Data1 = Text(item["waist_f"]),
                Data2 = Text(item["thigh_f"]),
                Data3 = Text(item["shank_f"]),
                Data4 = Text(item["hip_f"]),
                Data5 = Text(item["brass_f"]),
                Data6 = Text(item["arm_f"]),
                Data7 = recordOn
            };
            ctx.Log($"待写入：{Newtonsoft.Json.JsonConvert.SerializeObject(customData)}");
            datas.Add(customData);
        }

        if (datas.Count > 0)
        {
            await ctx.CustomData.AddAsync(datas, ct);
            ctx.Log($"已写入 {datas.Count} 条三围数据。");
        }

        await ctx.CustomData.SaveTitleAsync(CustomDataType, "三围数据", Titles, ct);
    }

    private static async Task<JArray> GetDataAsync(QuantumTaskContext ctx, string token, string deviceToken, string anonToken, string cookie, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}?per=10&token={token}&page=1");
            foreach (var header in Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
            request.Headers.TryAddWithoutValidation("Device-Token", deviceToken);
            request.Headers.TryAddWithoutValidation("Anonymous-Token", anonToken);
            request.Headers.TryAddWithoutValidation("Token", token);
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await ctx.Http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                ctx.Log($"三围数据请求失败：HTTP {(int)response.StatusCode} {body}");
                return null;
            }
            return JObject.Parse(body)["data"] as JArray;
        }
        catch (Exception e)
        {
            ctx.Log($"三围数据请求异常：{e.Message}");
            return null;
        }
    }

    /// <summary>字段原样转字符串（数字保持紧凑表示，字符串去引号），null/undefined 记为 null。</summary>
    private static string Text(JToken token)
        => token == null || token.Type is JTokenType.Null or JTokenType.Undefined ? null : token.ToString();

    /// <summary>取环境变量值，空白视为未配置。</summary>
    private static string Var(QuantumTaskContext ctx, string name)
        => ctx.Variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

