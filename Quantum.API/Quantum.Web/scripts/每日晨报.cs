// ============================================================================
// 每日晨报（2026-09-17 新写）：定时把「日期/农历/节假日/天气/头条」汇总成一条消息推进 App 会话流。
// 后端要求：只用基础契约（ctx.Http / ctx.Notify / ctx.Log / ctx.Variables），无 CustomData 依赖，
//   任意已部署脚本引擎的后端均可保存执行。
// 数据源（2026-09-17 实测选型，全部免 key）：
//   农历/干支/生肖 —— BCL ChineseLunisolarCalendar 本地计算，不依赖网络；
//   节假日/调休 —— NateScarlet/holiday-cn（国务院公告的结构化镜像）：
//     默认依次试 jsdelivr、raw.githubusercontent 两个镜像（timor.tech 已被 Cloudflare 盾弃用），
//     该数据按年发布，脚本自动取当年+次年两份合并（跨年/年初次年第 404 属正常，忽略）；
//   天气 —— Open-Meteo（geocoding-api + api，免 key，支持中文城市名检索经纬度）；
//   头条 —— 60s API（github.com/vikiboss/60s「每天60秒读懂世界」，取第一条），失败静默跳过；
//     2026-09-19 增：头条下附当天微信原文链接（data.link）作「当天要闻全文」；
//     2026-09-20 改命名链接 {{link:文字|URL}}（App/Web 2026-09-20 起支持：只显示文字、点击跳转，
//     不占链接地址版面；旧版客户端按字面显示标记）。
// 环境变量：
//   scripts_morning_city（可选，默认「成都」，中文城市名，经 Open-Meteo 检索）；
//   scripts_morning_coord（可选，「纬度,经度」直填坐标，填了就跳过城市检索；
//     新悦广场签到脚本坐标 30.64,104.04 即成都，故默认城市取成都）；
//   scripts_holiday_url（可选，holiday-cn 数据目录地址，脚本向其拼 /{年份}.json，用于镜像失效轮换）；
//   scripts_60s_url（可选，60s 接口地址，域名轮换时改这里）。
// 推荐任务配置：定时 Cron 0 30 7 * * ?（每天 07:30，Quartz 六段式，按服务器本地时区），
//   开启推送即可；无需指令触发。
// 设计取舍：
//   1) 三个外部源彼此独立、各自 try/catch，任一失败只降级不失败（如节假日源挂了仍推农历+天气）；
//   2) 生活提示按阈值推导：降水概率≥40% 提示带伞、温差≥10℃ 提示添衣、≥35℃ 高温、≤5℃ 低温、风≥30km/h 大风；
//   3) 日期一律取服务器本地时间——Docker 部署请确认容器时区为 Asia/Shanghai，否则晨报日期/Cron 都会偏 8 小时；
//   4) Open-Meteo 的 daily.time 是城市当地日期序列，按「今天」匹配下标而非固定取第 0 位，避免凌晨跨日错位。
// ============================================================================
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class MorningReportTask : IQuantumTask
{
    private const string DefaultCity = "成都";
    private const string GeocodingUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
    private const string HolidayPrimaryBase = "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master";
    private const string HolidayBackupBase = "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master";
    private const string SixtySecondsUrl = "https://60s.viki.moe/v2/60s";

    private static readonly string[] HeavenlyStems = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
    private static readonly string[] EarthlyBranches = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };
    private static readonly string[] ZodiacAnimals = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    private static readonly string[] LunarMonthNames = { "正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月" };
    private static readonly string[] LunarDigitNames = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
    private static readonly string[] WeekNames = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var today = DateTime.Now;
        var report = new StringBuilder();
        report.Append($"☀️ 每日晨报 · {today.Month}月{today.Day}日 {WeekNames[(int)today.DayOfWeek]}\n");

        // ── 农历/干支/生肖（纯本地计算，必成功） ──
        report.Append(LunarText(today)).Append('\n');

        // ── 节假日/调休（holiday-cn，失败降级为周末/工作日判断） ──
        var holidays = await LoadHolidayTableAsync(ctx, ct);
        report.Append(HolidayText(holidays, today)).Append('\n');

        // ── 天气（Open-Meteo，失败降级为整段缺天气） ──
        var (coordError, weatherLines) = await WeatherTextAsync(ctx, today, ct);
        if (coordError != null)
        {
            report.Append($"🌤 天气获取失败：{coordError}\n");
        }
        else
        {
            foreach (var line in weatherLines)
            {
                report.Append(line).Append('\n');
            }
        }

        // ── 头条（60s API 第一条，失败静默；「当天要闻全文」为命名链接，点击直达不占地址版面） ──
        var (headline, headlineLink) = await HeadlineAsync(ctx, ct);
        if (!string.IsNullOrEmpty(headline))
        {
            report.Append($"📰 昨夜今晨｜{headline}\n");
            if (!string.IsNullOrEmpty(headlineLink))
            {
                report.Append($"🔗 {Link("当天要闻全文", headlineLink)}\n");
            }
        }

        var message = report.ToString().TrimEnd();
        ctx.Log(message);
        await ctx.Notify.SendAsync("每日晨报", message, ct);
    }

    // ────────────────────────── 农历 ──────────────────────────

    /// <summary>农历日期 + 干支年 + 生肖，如「农历七月廿七 · 丙午马年」（闰月带「闰」前缀）。</summary>
    private static string LunarText(DateTime date)
    {
        var cal = new ChineseLunisolarCalendar();
        int lunarYear = cal.GetYear(date);
        int month = cal.GetMonth(date);
        int day = cal.GetDayOfMonth(date);

        // GetLeapMonth 返回闰月的序号位（如闰六月返回 7）：月份序号大于等于该位时实际月名要前移一位
        int leap = cal.GetLeapMonth(lunarYear);
        string monthName;
        if (leap == 0 || month < leap)
        {
            monthName = LunarMonthNames[month - 1];
        }
        else if (month == leap)
        {
            monthName = "闰" + LunarMonthNames[month - 2];
        }
        else
        {
            monthName = LunarMonthNames[month - 2];
        }

        int stem = (lunarYear - 4) % 10;
        int branch = (lunarYear - 4) % 12;
        return $"农历{monthName}{LunarDayName(day)} · {HeavenlyStems[stem]}{EarthlyBranches[branch]}{ZodiacAnimals[branch]}年";
    }

    /// <summary>农历日中文名：初一~初十 / 十一~十九 / 二十 / 廿一~廿九 / 三十。</summary>
    private static string LunarDayName(int day)
    {
        if (day == 10)
        {
            return "初十";
        }
        if (day == 20)
        {
            return "二十";
        }
        if (day == 30)
        {
            return "三十";
        }
        if (day < 10)
        {
            return "初" + LunarDigitNames[day];
        }
        if (day < 20)
        {
            return "十" + LunarDigitNames[day - 10];
        }
        return "廿" + LunarDigitNames[day - 20];
    }

    // ────────────────────────── 节假日 ──────────────────────────

    /// <summary>加载当年+次年 holiday-cn 数据合并为「yyyy-MM-dd → (假日名, 是否休息)」，全失败返回空表。</summary>
    private static async Task<Dictionary<string, (string Name, bool IsOff)>> LoadHolidayTableAsync(
        QuantumTaskContext ctx, CancellationToken ct)
    {
        var baseUrls = new List<string> { HolidayPrimaryBase, HolidayBackupBase };
        if (ctx.Variables.TryGetValue("scripts_holiday_url", out var configured) && !string.IsNullOrEmpty(configured))
        {
            baseUrls = [configured.TrimEnd('/')];
        }

        var table = new Dictionary<string, (string, bool)>();
        int year = DateTime.Now.Year;
        foreach (var y in new[] { year, year + 1 })
        {
            foreach (var baseUrl in baseUrls)
            {
                ct.ThrowIfCancellationRequested();
                var json = await GetJsonAsync(ctx, $"{baseUrl}/{y}.json", ct);
                if (json?["days"] is not JArray days)
                {
                    continue;
                }
                foreach (var item in days)
                {
                    var key = item["date"]?.ToString();
                    if (!string.IsNullOrEmpty(key))
                    {
                        table[key] = (item["name"]?.ToString() ?? "假日", item["isOffDay"]?.Type == JTokenType.Boolean && item["isOffDay"].Value<bool>());
                    }
                }
                break; // 当年任一镜像成功即不再换源
            }
        }
        if (table.Count == 0)
        {
            ctx.Log("holiday-cn 两个镜像都未取到数据，节假日/调休提示将按周末判断。");
        }
        return table;
    }

    /// <summary>今天与明天的休息/上班描述（含调休、假期收尾提示），数据缺失退化为周末判断。</summary>
    private static string HolidayText(Dictionary<string, (string Name, bool IsOff)> table, DateTime today)
    {
        var tomorrow = today.AddDays(1);
        string todayText = DescribeDay(table, today);
        string tomorrowText = DescribeDay(table, tomorrow);
        string line = $"📅 今天：{todayText}｜明天：{tomorrowText}";

        bool todayOff = IsOffDay(table, today);
        bool tomorrowOff = IsOffDay(table, tomorrow);
        if (todayOff && !tomorrowOff)
        {
            line += "（假期最后一天）";
        }
        else if (!todayOff && tomorrowOff)
        {
            line += "（明天开始休假）";
        }
        return line;
    }

    private static string DescribeDay(Dictionary<string, (string Name, bool IsOff)> table, DateTime date)
    {
        if (table.TryGetValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var entry))
        {
            return entry.IsOff
                ? $"{QuantumText.Tag("green", "休")} {entry.Name}"
                : $"{QuantumText.Tag("orange", "班")} {entry.Name}调休";
        }
        bool weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return weekend ? "周末" : "工作日";
    }

    private static bool IsOffDay(Dictionary<string, (string Name, bool IsOff)> table, DateTime date)
    {
        if (table.TryGetValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var entry))
        {
            return entry.IsOff;
        }
        return date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }

    // ────────────────────────── 天气 ──────────────────────────

    /// <summary>今日+明日天气两行与生活提示；城市坐标解析失败时返回错误说明。</summary>
    private static async Task<(string Error, string[] Lines)> WeatherTextAsync(
        QuantumTaskContext ctx, DateTime today, CancellationToken ct)
    {
        string latitude;
        string longitude;
        if (ctx.Variables.TryGetValue("scripts_morning_coord", out var coord) && !string.IsNullOrEmpty(coord) && coord.Contains(','))
        {
            var parts = coord.Split(',');
            latitude = parts[0].Trim();
            longitude = parts[1].Trim();
        }
        else
        {
            var city = ctx.Variables.TryGetValue("scripts_morning_city", out var configured) && !string.IsNullOrEmpty(configured)
                ? configured
                : DefaultCity;
            var geo = await GetJsonAsync(ctx,
                $"{GeocodingUrl}?name={Uri.EscapeDataString(city)}&count=1&language=zh&format=json", ct);
            var place = geo?["results"]?.First;
            if (place == null)
            {
                return ($"未检索到城市「{city}」的坐标，请检查 scripts_morning_city 或改填 scripts_morning_coord", null);
            }
            latitude = place.Value<double>("latitude").ToString(CultureInfo.InvariantCulture);
            longitude = place.Value<double>("longitude").ToString(CultureInfo.InvariantCulture);
            ctx.Log($"城市「{city}」坐标：{latitude},{longitude}");
        }

        var forecastUrl = $"{ForecastUrl}?latitude={latitude}&longitude={longitude}"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,wind_speed_10m_max"
            + "&current=temperature_2m,weather_code&timezone=auto&forecast_days=2";
        var root = await GetJsonAsync(ctx, forecastUrl, ct);
        var daily = root?["daily"];
        if (daily?["time"] is not JArray times)
        {
            return ("天气接口未返回预报数据", null);
        }

        // daily.time 是城市当地日期序列，按今天匹配下标（匹配不到退回第 0 位）
        int todayIndex = 0;
        var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        for (int i = 0; i < times.Count; i++)
        {
            if (times[i]?.ToString() == todayKey)
            {
                todayIndex = i;
                break;
            }
        }

        int code = daily["weather_code"]?[todayIndex]?.Type == JTokenType.Integer ? daily["weather_code"].Value<int>(todayIndex) : -1;
        double maxT = DailyNumber(daily, "temperature_2m_max", todayIndex);
        double minT = DailyNumber(daily, "temperature_2m_min", todayIndex);
        double rain = DailyNumber(daily, "precipitation_probability_max", todayIndex);
        double wind = DailyNumber(daily, "wind_speed_10m_max", todayIndex);
        double currentT = root["current"]?["temperature_2m"]?.Type == JTokenType.Float || root["current"]?["temperature_2m"]?.Type == JTokenType.Integer
            ? root["current"].Value<double>("temperature_2m")
            : double.NaN;

        string current = double.IsNaN(currentT) ? string.Empty : $" {FormatTemp(currentT)}℃";
        var lines = new List<string>
        {
            $"🌤 {CityLabel(ctx)}{DescribeWeather(code)}{current}｜今日 {FormatTemp(minT)}~{FormatTemp(maxT)}℃ · 降水{rain:F0}% · 风{wind:F0}km/h",
        };

        int nextIndex = todayIndex + 1;
        if (nextIndex < times.Count)
        {
            int nextCode = daily["weather_code"]?[nextIndex]?.Type == JTokenType.Integer ? daily["weather_code"].Value<int>(nextIndex) : -1;
            double nextMax = DailyNumber(daily, "temperature_2m_max", nextIndex);
            double nextMin = DailyNumber(daily, "temperature_2m_min", nextIndex);
            double nextRain = DailyNumber(daily, "precipitation_probability_max", nextIndex);
            lines.Add($"☔ 明日：{DescribeWeather(nextCode)} {FormatTemp(nextMin)}~{FormatTemp(nextMax)}℃ · 降水{nextRain:F0}%");
        }

        var tips = new List<string>();
        if (rain >= 40)
        {
            tips.Add("降水概率较高，出门带伞");
        }
        if (maxT - minT >= 10)
        {
            tips.Add($"温差 {FormatTemp(maxT - minT)}℃，早晚添衣");
        }
        if (maxT >= 35)
        {
            tips.Add("高温天气，注意防暑");
        }
        if (minT <= 5)
        {
            tips.Add("气温偏低，注意保暖");
        }
        if (wind >= 30)
        {
            tips.Add("风力较大，留意高空坠物");
        }
        if (tips.Count > 0)
        {
            lines.Add("💡 " + string.Join("；", tips));
        }
        return (null, lines.ToArray());
    }

    private static string CityLabel(QuantumTaskContext ctx)
        => ctx.Variables.TryGetValue("scripts_morning_coord", out var coord) && !string.IsNullOrEmpty(coord)
            ? "本地 · "
            : (ctx.Variables.TryGetValue("scripts_morning_city", out var city) && !string.IsNullOrEmpty(city) ? city : DefaultCity) + " · ";

    private static double DailyNumber(JToken daily, string key, int index)
    {
        var token = daily[key]?[index];
        if (token == null || token.Type is JTokenType.Null or JTokenType.Undefined)
        {
            return 0;
        }
        return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static string FormatTemp(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>WMO 天气代码 → 中文描述。</summary>
    private static string DescribeWeather(int code) => code switch
    {
        0 => "晴",
        1 => "多云转晴",
        2 => "局部多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻毛毛雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 or 67 => "冻雨",
        71 => "小雪",
        73 => "中雪",
        75 => "大雪",
        77 => "米雪",
        80 or 81 or 82 => "阵雨",
        85 or 86 => "阵雪",
        95 => "雷阵雨",
        96 or 99 => "雷阵雨伴冰雹",
        _ => "天气未知"
    };

    // ────────────────────────── 头条 ──────────────────────────

    /// <summary>60s API 第一条新闻 + 当天微信原文链接（data.link）；任何失败返回空串（头条是锦上添花，不因它失败）。</summary>
    private static async Task<(string Headline, string ArticleLink)> HeadlineAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            var url = ctx.Variables.TryGetValue("scripts_60s_url", out var configured) && !string.IsNullOrEmpty(configured)
                ? configured
                : SixtySecondsUrl;
            var json = await GetJsonAsync(ctx, url, ct);
            var first = json?["data"]?["news"]?.First?.ToString();
            if (string.IsNullOrEmpty(first))
            {
                return (string.Empty, string.Empty);
            }
            var headline = first.Length > 80 ? first[..80] + "…" : first;
            return (headline, json?["data"]?["link"]?.ToString()?.Trim() ?? string.Empty);
        }
        catch (Exception e)
        {
            ctx.Log($"头条获取失败（忽略）：{e.Message}");
            return (string.Empty, string.Empty);
        }
    }

    // ────────────────────────── HTTP 公共 ──────────────────────────

    /// <summary>命名链接标记 {{link:文字|URL}}：App/Web 气泡内只显示文字、点击跳 URL。
    /// （脚本内联生成纯字符串，不依赖后端 QuantumText 版本——两端口径见各自富文本解析器。）</summary>
    private static string Link(string text, string url) => $"{{{{link:{text}|{url}}}}}";

    /// <summary>GET JSON：显式带 UA/Accept（部分站点对空头的请求回 403，见 btsow 转换记录），失败/非 2xx 返回 null。</summary>
    private static async Task<JObject> GetJsonAsync(QuantumTaskContext ctx, string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; QuantumTask/1.0)");
            using var response = await ctx.Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                ctx.Log($"GET {HostOf(url)} → HTTP {(int)response.StatusCode}");
                return null;
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrEmpty(body) ? null : JObject.Parse(body);
        }
        catch (Exception e)
        {
            ctx.Log($"GET {HostOf(url)} 异常：{e.Message}");
            return null;
        }
    }

    private static string HostOf(string url)
    {
        var trimmed = url ?? string.Empty;
        int start = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
        {
            return trimmed;
        }
        start += 3;
        int end = trimmed.IndexOf('/', start);
        return end < 0 ? trimmed[start..] : trimmed[start..end];
    }
}
