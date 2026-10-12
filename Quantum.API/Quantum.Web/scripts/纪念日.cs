// ============================================================================
// 生日·纪念日（anniversary）：记录生日/纪念日并定时提醒，支持农历与阳历。
//
// 指令模式（任务触发指令建议 ^纪念日.*，指令环境变量名 anniversary_msg）：
//   纪念日 帮助                        查看用法
//   纪念日 列表                        列出全部记录
//   纪念日 查询 张三                   按名称关键字查询
//   纪念日 添加 张三 生日 农历 1990-08-16 备注     词语顺序随意；类型默认「生日」
//   纪念日 添加 结婚纪念日 阳历 2020-05-20          （名称含「纪念/周年」时类型默认「纪念日」）
//   纪念日 删除 张三                   删除同名记录（多条同名时列出并不删，请先在数据管理页区分）
//   农历日期写法：1990-08-16；闰月：1990-闰08-16；类型词=生日/生辰或含「纪念」「周年」的词，
//   其余识别不了的词并入备注。
//
// 提醒模式（Cron 定时触发，建议 0 0 8 * * ? 每天 08:00，无指令变量时自动进入）：
//   扫描全部记录，今天与未来 N 天内到期的汇总推送一条通知（无到期只写日志，不打扰）。
//   N = 环境变量 anniversary_advance_days（默认 7，范围 0-60）。
//
// 存储：自定义数据 anniversary（Data1 名称 / Data2 类型 / Data3 历法 / Data4 日期 / Data5 备注），
//   首次运行自动建表头，可直接在「数据管理」页手工维护。农历日期原样存 Data4（如 1990-闰05-16）。
//
// 农历算法：内置 1900-2100 年 lunarInfo 数据表（与 solarlunar 3.1.0 / jjonline calendar.js
//   同源，MIT），完全离线换算，已用春节/中秋/闰月锚点验证。边界自动回退并在文案注明：
//   当年无对应闰月按平月、腊月三十缺失按廿九、阳历 2/29 平年按 2/28。
//
// 手动调试：配变量 scripts_anniversary=<完整指令文本> 后执行。
// ============================================================================
using Quantum.Plugins;
using System.Text.RegularExpressions;

public class AnniversaryTask : IQuantumTask
{
    private const string DataType = "anniversary";

    /// <summary>农历 1900 年正月初一对应的阳历。</summary>
    private static readonly DateTime BaseDate = new(1900, 1, 31);

    private static readonly string[] MonCn = { "正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "腊" };

    private static readonly string[] NumCn =
    {
        "", "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    };

    // ---------------------------------------------------------------------------
    // 农历数据表 lunarInfo[year-1900]（1900-2100，与 solarlunar 3.1.0 / jjonline 同源）：
    //   bit15-bit4  依次为正月..十二月大小月（1=30天，0=29天）
    //   bit3-bit1   闰月月份（0 表示当年无闰月）
    //   bit0        闰月大小（1=30天）
    // ---------------------------------------------------------------------------
    private static readonly int[] LunarInfo =
    {
        /*1900-1909*/ 0x04bd8, 0x04ae0, 0x0a570, 0x054d5, 0x0d260, 0x0d950, 0x16554, 0x056a0, 0x09ad0, 0x055d2,
        /*1910-1919*/ 0x04ae0, 0x0a5b6, 0x0a4d0, 0x0d250, 0x1d255, 0x0b540, 0x0d6a0, 0x0ada2, 0x095b0, 0x14977,
        /*1920-1929*/ 0x04970, 0x0a4b0, 0x0b4b5, 0x06a50, 0x06d40, 0x1ab54, 0x02b60, 0x09570, 0x052f2, 0x04970,
        /*1930-1939*/ 0x06566, 0x0d4a0, 0x0ea50, 0x06e95, 0x05ad0, 0x02b60, 0x186e3, 0x092e0, 0x1c8d7, 0x0c950,
        /*1940-1949*/ 0x0d4a0, 0x1d8a6, 0x0b550, 0x056a0, 0x1a5b4, 0x025d0, 0x092d0, 0x0d2b2, 0x0a950, 0x0b557,
        /*1950-1959*/ 0x06ca0, 0x0b550, 0x15355, 0x04da0, 0x0a5b0, 0x14573, 0x052b0, 0x0a9a8, 0x0e950, 0x06aa0,
        /*1960-1969*/ 0x0aea6, 0x0ab50, 0x04b60, 0x0aae4, 0x0a570, 0x05260, 0x0f263, 0x0d950, 0x05b57, 0x056a0,
        /*1970-1979*/ 0x096d0, 0x04dd5, 0x04ad0, 0x0a4d0, 0x0d4d4, 0x0d250, 0x0d558, 0x0b540, 0x0b6a0, 0x195a6,
        /*1980-1989*/ 0x095b0, 0x049b0, 0x0a974, 0x0a4b0, 0x0b27a, 0x06a50, 0x06d40, 0x0af46, 0x0ab60, 0x09570,
        /*1990-1999*/ 0x04af5, 0x04970, 0x064b0, 0x074a3, 0x0ea50, 0x06b58, 0x05ac0, 0x0ab60, 0x096d5, 0x092e0,
        /*2000-2009*/ 0x0c960, 0x0d954, 0x0d4a0, 0x0da50, 0x07552, 0x056a0, 0x0abb7, 0x025d0, 0x092d0, 0x0cab5,
        /*2010-2019*/ 0x0a950, 0x0b4a0, 0x0baa4, 0x0ad50, 0x055d9, 0x04ba0, 0x0a5b0, 0x15176, 0x052b0, 0x0a930,
        /*2020-2029*/ 0x07954, 0x06aa0, 0x0ad50, 0x05b52, 0x04b60, 0x0a6e6, 0x0a4e0, 0x0d260, 0x0ea65, 0x0d530,
        /*2030-2039*/ 0x05aa0, 0x076a3, 0x096d0, 0x04afb, 0x04ad0, 0x0a4d0, 0x1d0b6, 0x0d250, 0x0d520, 0x0dd45,
        /*2040-2049*/ 0x0b5a0, 0x056d0, 0x055b2, 0x049b0, 0x0a577, 0x0a4b0, 0x0aa50, 0x1b255, 0x06d20, 0x0ada0,
        /*2050-2059*/ 0x14b63, 0x09370, 0x049f8, 0x04970, 0x064b0, 0x168a6, 0x0ea50, 0x06b20, 0x1a6c4, 0x0aae0,
        /*2060-2069*/ 0x092e0, 0x0d2e3, 0x0c960, 0x0d557, 0x0d4a0, 0x0da50, 0x05d55, 0x056a0, 0x0a6d0, 0x055d4,
        /*2070-2079*/ 0x052d0, 0x0a9b8, 0x0a950, 0x0b4a0, 0x0b6a6, 0x0ad50, 0x055a0, 0x0aba4, 0x0a5b0, 0x052b0,
        /*2080-2089*/ 0x0b273, 0x06930, 0x07337, 0x06aa0, 0x0ad50, 0x14b55, 0x04b60, 0x0a570, 0x054e4, 0x0d160,
        /*2090-2099*/ 0x0e968, 0x0d520, 0x0daa0, 0x16aa6, 0x056d0, 0x04ae0, 0x0a9d4, 0x0a4d0, 0x0d150, 0x0f252,
        /*2100    */ 0x0d520
    };

    private const string HelpText =
        "【纪念日·用法】\r\n" +
        "纪念日 添加 <名称> [类型] [农历|阳历] <日期> [备注]\r\n" +
        "  类型：生日（默认），或含「纪念/周年」的词（结婚纪念日、恋爱纪念日…）；名称本身含「纪念/周年」时默认按纪念日\r\n" +
        "  日期：1990-08-16；农历闰月写 1990-闰08-16\r\n" +
        "纪念日 列表 ｜ 纪念日 查询 <名称> ｜ 纪念日 删除 <名称>\r\n" +
        "示例：纪念日 添加 张三 生日 农历 1990-08-16\r\n" +
        "　　　纪念日 添加 结婚纪念日 阳历 2020-05-20 领证\r\n" +
        "定时提醒：每天 08:00 推送今天与未来 7 天内到期的生日/纪念日（提前天数可用环境变量 anniversary_advance_days 调整，0-60）。";

    // ---------------------------------------------------------------- 入口
    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        await EnsureTitleAsync(ctx, ct);
        var input = Var(ctx, "anniversary_msg", "scripts_anniversary");
        if (string.IsNullOrWhiteSpace(input))
        {
            await RunRemindAsync(ctx, ct);
            return;
        }
        await RunCommandAsync(ctx, input.Trim(), ct);
    }

    // ---------------------------------------------------------------- 提醒模式
    private static async Task RunRemindAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        var advance = ReadInt(ctx, "anniversary_advance_days", 7, 0, 60);
        var today = DateTime.Today;
        ctx.Log($"提醒模式：提前 {advance} 天；今日 {today:yyyy-MM-dd}（农历{SolarToLunarCn(today)}）。");

        var rows = await ctx.CustomData.QueryAsync(DataType, ct: ct);
        var due = new List<(int Left, Rec Rec, DateTime Next, string Note, int Years)>();
        var bad = new List<Rec>();
        foreach (var row in rows)
        {
            var rec = Parse(row);
            if (rec.Error != null) { bad.Add(rec); continue; }
            try
            {
                var (next, note, years) = NextOccurrence(rec, today);
                var left = (next - today).Days;
                if (left >= 0 && left <= advance)
                {
                    due.Add((left, rec, next, note, years));
                }
            }
            catch (Exception e)
            {
                rec.Error = e.Message;
                bad.Add(rec);
            }
        }
        if (due.Count == 0 && bad.Count == 0)
        {
            ctx.Log($"今日与未来 {advance} 天内无到期事项（共 {rows.Count} 条记录），不推送。");
            return;
        }

        due.Sort((a, b) => a.Left != b.Left ? a.Left - b.Left : string.Compare(a.Rec.Name, b.Rec.Name, StringComparison.Ordinal));
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"生日·纪念日提醒（提前 {advance} 天）");
        var n = 0;
        foreach (var d in due.Where(x => x.Left == 0))
        {
            n++;
            sb.AppendLine($"{n}. {LineOf(d, true)}");
        }
        var future = due.Where(x => x.Left > 0).ToList();
        if (future.Count > 0)
        {
            sb.AppendLine($"——未来 {advance} 天内——");
            foreach (var d in future)
            {
                n++;
                sb.AppendLine($"{n}. {LineOf(d, false)}");
            }
        }
        if (bad.Count > 0)
        {
            sb.AppendLine($"——{bad.Count} 条记录日期异常，请用「纪念日 列表」检查——");
            foreach (var b in bad)
            {
                sb.AppendLine($"· {b.Name}：{b.Error}");
            }
        }
        await NotifyAsync(ctx, "生日·纪念日提醒", sb.ToString().TrimEnd(), ct);
    }

    /// <summary>一条到期事项的展示行。</summary>
    private static string LineOf((int Left, Rec Rec, DateTime Next, string Note, int Years) d, bool today)
    {
        var cal = d.Rec.Cal == "农历" ? $"农历{LunarCnOf(d.Rec)}" : "";
        var when = today ? "今天" : $"{d.Left} 天后（{d.Next:MM-dd}）";
        var unit = d.Rec.Kind.Contains("生日") ? "周岁" : "周年";
        var note = d.Note.Length > 0 ? $"〔{d.Note}〕" : "";
        var remark = string.IsNullOrWhiteSpace(d.Rec.Remark) ? "" : $"（{d.Rec.Remark}）";
        var head = cal.Length > 0 ? $"{d.Rec.Name}{KindLabel(d.Rec)}·{cal}" : d.Rec.Name + KindLabel(d.Rec);
        return $"{head} {when} {YearsCn(d.Years, unit)}{note}{remark}";
    }

    // ---------------------------------------------------------------- 指令模式
    private static async Task RunCommandAsync(QuantumTaskContext ctx, string input, CancellationToken ct)
    {
        var text = Regex.Replace(input, @"^\s*纪念日\s*", "", RegexOptions.IgnoreCase).Trim();
        if (text.Length == 0 || text.Contains("帮助") || text is "?" or "？")
        {
            await NotifyAsync(ctx, "纪念日·用法", HelpText, ct);
            return;
        }
        var tokens = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var verb = tokens[0];
        var rest = string.Join(" ", tokens.Skip(1));
        switch (verb)
        {
            case "添加" or "新增" or "增加" or "add":
                await CmdAdd(ctx, tokens.Skip(1).ToList(), ct);
                break;
            case "删除" or "删" or "移除" or "del":
                await CmdDelete(ctx, rest, ct);
                break;
            case "查询" or "查" or "找" or "看":
                await CmdQuery(ctx, rest, ct);
                break;
            case "列表" or "全部" or "所有" or "list":
                await CmdQuery(ctx, null, ct);
                break;
            default:
                await NotifyAsync(ctx, "纪念日·用法", $"无法识别「{verb}」，请检查指令。\r\n{HelpText}", ct);
                break;
        }
    }

    private static async Task CmdAdd(QuantumTaskContext ctx, List<string> tokens, CancellationToken ct)
    {
        if (tokens.Count < 2)
        {
            await NotifyAsync(ctx, "纪念日·添加失败",
                "格式：纪念日 添加 <名称> [类型] [农历|阳历] <日期> [备注]\r\n示例：纪念日 添加 张三 生日 农历 1990-08-16", ct);
            return;
        }
        var name = tokens[0].Trim();
        var kind = "生日";
        var kindExplicit = false;
        var cal = "阳历";
        var leap = false;
        var hasDate = false;
        int y = 0, m = 0, d = 0;
        var leftovers = new List<string>();
        foreach (var t in tokens.Skip(1))
        {
            var dm = Regex.Match(t, @"^(\d{4})[-/.年](闰?)(\d{1,2})[-/.月](\d{1,2})日?$");
            if (dm.Success)
            {
                y = int.Parse(dm.Groups[1].Value);
                leap = dm.Groups[2].Value == "闰";
                m = int.Parse(dm.Groups[3].Value);
                d = int.Parse(dm.Groups[4].Value);
                hasDate = true;
                continue;
            }
            if (t is "农历" or "阴历") { cal = "农历"; continue; }
            if (t is "阳历" or "公历" or "新历" or "国历") { cal = "阳历"; continue; }
            if (t is "生日" or "生辰") { kind = "生日"; kindExplicit = true; continue; }
            if (t.Contains("纪念") || t.Contains("周年")) { kind = t; kindExplicit = true; continue; }
            leftovers.Add(t);
        }
        // 未显式给类型词时：名称含「纪念/周年」按纪念日，否则按生日
        if (!kindExplicit && (name.Contains("纪念") || name.Contains("周年")))
        {
            kind = "纪念日";
        }
        if (!hasDate)
        {
            await NotifyAsync(ctx, "纪念日·添加失败", "未识别到日期，请写 1990-08-16（农历闰月 1990-闰08-16）。", ct);
            return;
        }
        if (leap && cal == "阳历")
        {
            await NotifyAsync(ctx, "纪念日·添加失败", "阳历没有闰月写法，闰月只用于农历日期。", ct);
            return;
        }
        if (cal == "农历")
        {
            if (y < 1900 || y > 2100 || m < 1 || m > 12 || d < 1 || d > 30)
            {
                await NotifyAsync(ctx, "纪念日·添加失败", $"农历 {y}-{m}-{d} 不合法（年份 1900-2100，月 1-12，日 1-30）。", ct);
                return;
            }
        }
        else
        {
            if (m < 1 || m > 12 || d < 1 || d > 31 || !DateTime.TryParse($"{y:0000}-{m:00}-{d:00}", out _))
            {
                await NotifyAsync(ctx, "纪念日·添加失败", $"阳历 {y}-{m}-{d} 不是有效日期。", ct);
                return;
            }
        }

        var rec = new Rec { Name = name, Kind = kind, Cal = cal, Y = y, M = m, D = d, Leap = leap, Remark = string.Join(" ", leftovers) };
        var stored = FormatDate(rec);

        // 查重（同名同历法同日期）
        var rows = await ctx.CustomData.QueryAsync(DataType, name, ct: ct);
        if (rows.Any(r => (r.Data1 ?? "").Trim() == name && (r.Data3 ?? "").Trim() == cal && (r.Data4 ?? "").Trim() == stored))
        {
            await NotifyAsync(ctx, "纪念日·添加失败", $"已存在同名记录：{name}（{cal} {stored}）。", ct);
            return;
        }

        await ctx.CustomData.AddAsync(new List<QuantumCustomDataValue>
        {
            new() { Type = DataType, Data1 = name, Data2 = kind, Data3 = cal, Data4 = stored, Data5 = rec.Remark }
        }, ct);

        // 回显：出生/发生当天阳历 + 下次发生
        string bornLine = "";
        if (cal == "农历")
        {
            try
            {
                var (born, _) = LunarToSolar(y, m, d, leap);
                bornLine = $"\r\n  原始日期对应阳历：{born:yyyy-MM-dd}";
            }
            catch (Exception e)
            {
                bornLine = $"\r\n  原始日期换算失败：{e.Message}";
            }
        }
        var today = DateTime.Today;
        var (next, note, years) = NextOccurrence(rec, today);
        var noteText = note.Length > 0 ? $"〔{note}〕" : "";
        var remarkText = string.IsNullOrWhiteSpace(rec.Remark) ? "" : $"（{rec.Remark}）";
        var calText = cal == "农历" ? $"·农历{LunarCnOf(rec)}" : "";
        var unit = kind.Contains("生日") ? "周岁" : "周年";
        await NotifyAsync(ctx, "纪念日·已添加",
            $"已添加：{name}{KindLabel(rec)}{calText}{remarkText}\r\n" +
            $"  下次：{next:yyyy-MM-dd}（还有 {(next - today).Days} 天，{YearsCn(years, unit)}）{noteText}" +
            bornLine, ct);
        ctx.Log($"已添加：{name} {kind} {cal} {stored}。");
    }

    private static async Task CmdDelete(QuantumTaskContext ctx, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            await NotifyAsync(ctx, "纪念日·删除失败", "格式：纪念日 删除 <名称>", ct);
            return;
        }
        var rows = await ctx.CustomData.QueryAsync(DataType, name, ct: ct);
        if (rows.Count == 0)
        {
            await NotifyAsync(ctx, "纪念日·删除", $"未找到名称含「{name}」的记录。", ct);
            return;
        }
        if (rows.Count > 1)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"名称含「{name}」的记录有 {rows.Count} 条，为避免误删本次未删除：");
            for (var i = 0; i < rows.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {rows[i].Data1}·{rows[i].Data2}·{rows[i].Data3} {rows[i].Data4}");
            }
            sb.Append("请先在「数据管理」页删除或改名到唯一。");
            await NotifyAsync(ctx, "纪念日·删除", sb.ToString(), ct);
            return;
        }
        var row = rows[0];
        await ctx.CustomData.DeleteAsync(new List<string> { row.Id }, ct);
        await NotifyAsync(ctx, "纪念日·已删除", $"已删除：{row.Data1}·{row.Data2}·{row.Data3} {row.Data4}", ct);
        ctx.Log($"已删除记录 {row.Id}：{row.Data1}。");
    }

    private static async Task CmdQuery(QuantumTaskContext ctx, string key, CancellationToken ct)
    {
        var rows = string.IsNullOrWhiteSpace(key)
            ? await ctx.CustomData.QueryAsync(DataType, ct: ct)
            : await ctx.CustomData.QueryAsync(DataType, key, ct: ct);
        if (rows.Count == 0)
        {
            await NotifyAsync(ctx, "纪念日·查询", string.IsNullOrWhiteSpace(key) ? "暂无记录，用「纪念日 添加」开始。" : $"未找到名称含「{key}」的记录。", ct);
            return;
        }
        var today = DateTime.Today;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.IsNullOrWhiteSpace(key) ? $"全部记录（{rows.Count} 条）：" : $"名称含「{key}」的记录（{rows.Count} 条）：");
        for (var i = 0; i < rows.Count; i++)
        {
            var rec = Parse(rows[i]);
            if (rec.Error != null)
            {
                sb.AppendLine($"{i + 1}. {rec.Name}·{rec.Kind}·{rec.Cal} {rec.Raw}（日期异常：{rec.Error}）");
                continue;
            }
            try
            {
                var (next, note, years) = NextOccurrence(rec, today);
                var calText = rec.Cal == "农历" ? $"·农历{LunarCnOf(rec)}" : "";
                var unit = rec.Kind.Contains("生日") ? "周岁" : "周年";
                var noteText = note.Length > 0 ? $"〔{note}〕" : "";
                var remarkText = string.IsNullOrWhiteSpace(rec.Remark) ? "" : $"（{rec.Remark}）";
                var left = (next - today).Days;
                var when = left == 0 ? "今天" : $"{left} 天后";
                sb.AppendLine($"{i + 1}. {rec.Name}{KindLabel(rec)}{calText} → {next:yyyy-MM-dd}（{when}，{YearsCn(years, unit)}）{noteText}{remarkText}");
            }
            catch (Exception e)
            {
                sb.AppendLine($"{i + 1}. {rec.Name}·{rec.Kind}·{rec.Cal} {rec.Raw}（换算失败：{e.Message}）");
            }
        }
        await NotifyAsync(ctx, "纪念日·查询", sb.ToString().TrimEnd(), ct);
    }

    // ---------------------------------------------------------------- 数据模型
    private sealed class Rec
    {
        public string Id;
        public string Name;
        public string Kind;
        public string Cal;      // 农历 / 阳历
        public string Raw;      // Data4 原文
        public string Remark;
        public int Y, M, D;
        public bool Leap;
        public string Error;    // 非空 = 解析失败
    }

    /// <summary>把自定义数据行解析为记录；失败时 Error 给出原因。</summary>
    private static Rec Parse(QuantumCustomDataValue row)
    {
        var rec = new Rec
        {
            Id = row.Id,
            Name = (row.Data1 ?? "").Trim(),
            Kind = (row.Data2 ?? "").Trim(),
            Cal = (row.Data3 ?? "").Trim(),
            Raw = (row.Data4 ?? "").Trim(),
            Remark = (row.Data5 ?? "").Trim()
        };
        if (rec.Kind.Length == 0) rec.Kind = "生日";
        if (rec.Cal is "农历" or "阴历") rec.Cal = "农历"; else rec.Cal = "阳历";
        if (rec.Name.Length == 0)
        {
            rec.Error = "名称为空";
            return rec;
        }
        var m = Regex.Match(rec.Raw, @"^(\d{4})-(闰)?(\d{1,2})-(\d{1,2})$");
        if (!m.Success)
        {
            rec.Error = $"日期格式 {rec.Raw}（应为 1990-08-16 / 1990-闰08-16）";
            return rec;
        }
        rec.Y = int.Parse(m.Groups[1].Value);
        rec.Leap = m.Groups[2].Value == "闰";
        rec.M = int.Parse(m.Groups[3].Value);
        rec.D = int.Parse(m.Groups[4].Value);
        if (rec.Cal == "农历")
        {
            if (rec.Y is < 1900 or > 2100 || rec.M is < 1 or > 12 || rec.D is < 1 or > 30)
            {
                rec.Error = $"农历 {rec.Raw} 越界（年 1900-2100，月 1-12，日 1-30）";
            }
        }
        else
        {
            if (rec.Leap)
            {
                rec.Error = $"阳历日期不应带闰：{rec.Raw}";
            }
            else if (!DateTime.TryParse($"{rec.Y:0000}-{rec.M:00}-{rec.D:00}", out _))
            {
                rec.Error = $"阳历 {rec.Raw} 不是有效日期";
            }
        }
        return rec;
    }

    private static string FormatDate(Rec rec)
        => $"{rec.Y:0000}-{(rec.Leap ? "闰" : "")}{rec.M:00}-{rec.D:00}";

    private static string LunarCnOf(Rec rec)
        => (rec.Leap ? "闰" : "") + MonCn[rec.M - 1] + "月" + NumCn[rec.D];

    /// <summary>类型标签：类型为生日或与名称重复时不重复展示。</summary>
    private static string KindLabel(Rec rec)
        => rec.Kind == "生日" || rec.Name.Contains(rec.Kind) || rec.Kind.Contains(rec.Name) ? "" : "·" + rec.Kind;

    /// <summary>周年/周岁文案：当年未满 1 周年显示「未满 1 周岁」而非「满 0 周岁」。</summary>
    private static string YearsCn(int years, string unit)
        => years <= 0 ? $"未满 1 {unit}" : $"满 {years} {unit}";

    // ---------------------------------------------------------------- 下次发生
    /// <summary>计算记录在 today（含）之后最近一次发生的阳历日期，及距出生/起始的整年数。</summary>
    private static (DateTime Next, string Note, int Years) NextOccurrence(Rec rec, DateTime today)
    {
        if (rec.Cal == "农历")
        {
            var (cand, note) = LunarToSolar(today.Year, rec.M, rec.D, rec.Leap);
            if (cand < today)
            {
                var (c2, n2) = LunarToSolar(today.Year + 1, rec.M, rec.D, rec.Leap);
                cand = c2;
                note = n2;
            }
            return (cand, note, cand.Year - rec.Y);
        }
        var sNote = "";
        DateTime Cand(int yy)
        {
            var dd = rec.D;
            if (rec.M == 2 && rec.D == 29 && !DateTime.IsLeapYear(yy))
            {
                dd = 28;
                sNote = "阳历 2 月 29 日，平年按 2 月 28 日";
            }
            return new DateTime(yy, rec.M, dd);
        }
        var solar = Cand(today.Year);
        if (solar < today)
        {
            sNote = "";
            solar = Cand(today.Year + 1);
        }
        return (solar, sNote, solar.Year - rec.Y);
    }

    // ---------------------------------------------------------------- 农历换算
    /// <summary>农历转阳历。目标农历日不存在时自动回退并在 note 说明（闰月缺失按平月、无三十按廿九）。</summary>
    private static (DateTime Date, string Note) LunarToSolar(int y, int m, int d, bool leap)
    {
        if (y is < 1900 or > 2100) throw new ArgumentOutOfRangeException(nameof(y), $"农历年份 {y} 超出 1900-2100");
        if (m is < 1 or > 12 || d is < 1 or > 30) throw new ArgumentOutOfRangeException($"农历日期 {y}-{m}-{d} 不合法");
        var note = "";
        var leapMonth = LeapMonthOf(y);
        if (leap && leapMonth != m)
        {
            leap = false;
            note = $"{y} 年无闰{MonCn[m - 1]}月，按平{MonCn[m - 1]}月";
        }
        var day = d;
        var mdays = leap ? LeapMonthDaysOf(y) : LunarMonthDays(y, m);
        if (day > mdays)
        {
            note = note.Length == 0 ? "" : note + "；";
            note += $"{MonCn[m - 1]}月无{NumCn[d]}，按{NumCn[mdays]}";
            day = mdays;
        }
        var days = 0;
        for (var i = 1900; i < y; i++)
        {
            days += LunarYearDays(i);
        }
        for (var mm = 1; mm < m; mm++)
        {
            days += LunarMonthDays(y, mm);
            if (mm == leapMonth)
            {
                days += LeapMonthDaysOf(y);
            }
        }
        if (leap)
        {
            days += LunarMonthDays(y, m); // 闰月排在平 m 月之后
        }
        days += day;
        return (BaseDate.AddDays(days - 1), note);
    }

    /// <summary>阳历转农历中文（如「闰六月廿三」）；超出 1900-2099 返回空串。</summary>
    private static string SolarToLunarCn(DateTime solar)
    {
        if (solar.Year is < 1900 or > 2099) return "";
        var offset = (int)(solar.Date - BaseDate).TotalDays + 1;
        var y = 1900;
        while (offset > LunarYearDays(y))
        {
            offset -= LunarYearDays(y);
            y++;
            if (y > 2099) return "";
        }
        var leapMonth = LeapMonthOf(y);
        var m = 1;
        var isLeap = false;
        for (; m <= 12; m++)
        {
            var md = LunarMonthDays(y, m);
            if (offset <= md) break;
            offset -= md;
            if (m == leapMonth)
            {
                var ld = LeapMonthDaysOf(y);
                if (offset <= ld) { isLeap = true; break; }
                offset -= ld;
            }
        }
        return (isLeap ? "闰" : "") + MonCn[m - 1] + "月" + NumCn[offset];
    }

    /// <summary>农历 y 年 m 月（1-12 平月）天数。</summary>
    private static int LunarMonthDays(int y, int m)
        => (LunarInfo[y - 1900] & (0x10000 >> m)) != 0 ? 30 : 29;

    /// <summary>农历 y 年闰月月份（0 = 无闰月）。</summary>
    private static int LeapMonthOf(int y)
        => LunarInfo[y - 1900] & 0xf;

    /// <summary>农历 y 年闰月天数（无闰月返回 0）。</summary>
    private static int LeapMonthDaysOf(int y)
        => LeapMonthOf(y) == 0 ? 0 : ((LunarInfo[y - 1900] & 0x10000) != 0 ? 30 : 29);

    /// <summary>农历 y 年总天数。</summary>
    private static int LunarYearDays(int y)
    {
        var days = 0;
        for (var m = 1; m <= 12; m++)
        {
            days += LunarMonthDays(y, m);
        }
        return days + LeapMonthDaysOf(y);
    }

    // ---------------------------------------------------------------- 通用工具
    /// <summary>确保自定义数据表头存在（幂等，失败不影响主流程）。</summary>
    private static async Task EnsureTitleAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.CustomData.SaveTitleAsync(DataType, "生日纪念日",
                new[] { "名称", "类型", "历法", "日期", "备注" }, ct);
        }
        catch (Exception e)
        {
            ctx.Log($"表头确保失败（不影响本次执行）：{e.Message}");
        }
    }

    private static async Task NotifyAsync(QuantumTaskContext ctx, string title, string content, CancellationToken ct)
    {
        ctx.Log($"[{title}] {content}");
        if (ctx.EnablePush)
        {
            await ctx.Notify.SendAsync(title, content, ct);
        }
    }

    private static int ReadInt(QuantumTaskContext ctx, string name, int fallback, int min, int max)
    {
        if (ctx.Variables.TryGetValue(name, out var text) && int.TryParse(text?.Trim(), out var value))
        {
            return Math.Clamp(value, min, max);
        }
        return fallback;
    }

    private static string Var(QuantumTaskContext ctx, params string[] names)
    {
        foreach (var name in names)
        {
            if (ctx.Variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }
}
