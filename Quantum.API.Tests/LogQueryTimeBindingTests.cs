using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Quantum.Entities.DTOs;

namespace Quantum.API.Tests;

/// <summary>
/// P4 §7.4（§3-6 时区）真实 Host 基线钉测：起真实 Kestrel + MVC 查询绑定管线（非 BCL 直测），
/// 断言前端 axios 以 <c>toISOString()</c> 发出的带 Z 后缀串，经绑定后保持 <c>Kind=Utc</c> 且逐 tick 等于原 UTC 瞬间。
///
/// 【复核结论修正】计划三次评审曾据 <c>DateTime.Parse('...Z')</c>→本地墙钟 判「偏 8 小时不属实」，
/// 但 MVC 绑定走 <c>TypeConverter.ConvertFromWithOptions</c>/<c>DateTimeStyles.RoundtripKind</c>，
/// 对本串实测结果是 <c>Kind=Utc</c>（不做本地换算），与 <c>DateTime.Now</c>（本地墙钟）入库的
/// <c>CreateTime</c> 比较侧不同源 → 原审计「偏 8 小时」在真实管线下成立。
/// 修复方向见 §7.4：前端改发本地格式串（<c>dayjs 'YYYY-MM-DD HH:mm:ss'</c>，不带 Z）→ 绑定为本地墙钟、与入库同源。
/// 本测试钉住「绑定层保留 UTC」这一真实事实，防将来有人按 BCL 探针误改。
/// </summary>
public class LogQueryTimeBindingTests
{
    [Fact]
    public async Task Z_Suffixed_QueryParam_Binds_As_Utc_PreservingInstant()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(TimeBindProbeController).Assembly);
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var baseUrl = app.Urls.First();

            // 前端 new Date(本地13:00).toISOString() 在 UTC+8 下即 "2026-03-15T05:00:00.000Z"
            var utcInstant = new DateTime(2026, 3, 15, 5, 0, 0, DateTimeKind.Utc);

            using var client = new HttpClient();
            var response = await client.GetAsync(
                $"{baseUrl}/probe/time?StartTime={Uri.EscapeDataString("2026-03-15T05:00:00.000Z")}");
            response.EnsureSuccessStatusCode();

            var json = JObject.Parse(await response.Content.ReadAsStringAsync());
            // MVC 默认 System.Text.Json 输出为 camelCase
            var boundTicks = json.Value<long?>("ticks");
            var boundKind = json.Value<int?>("kind");

            Assert.NotNull(boundTicks);
            // 实测：绑定层把带 Z 的串保持为 Kind=Utc（DateTimeKind.Utc=1），而非转本地墙钟
            Assert.Equal((int)DateTimeKind.Utc, boundKind!.Value);
            // 逐 tick 等于原 UTC 瞬间（无本地偏移），与 DateTime.Now 入库的本地墙钟不同源——缺陷即在此
            Assert.Equal(utcInstant.Ticks, boundTicks!.Value);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>
    /// §7.4 修复方向的正向基线：前端改发本地格式串（不带 Z）后，绑定值即落在本地墙钟、与 <c>DateTime.Now</c> 入库同源。
    /// 断言 <c>2026-03-15 13:00:00</c>（本地墙上时间）绑定后墙钟数值逐字段等于 13:00，且非 Kind=Utc（不偏移）。
    /// </summary>
    [Fact]
    public async Task LocalFormat_QueryParam_Binds_As_Local_WallClock()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(TimeBindProbeController).Assembly);
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var baseUrl = app.Urls.First();

            using var client = new HttpClient();
            // dayjs 'YYYY-MM-DD HH:mm:ss' 的产物：无 Z、无偏移标记
            var response = await client.GetAsync(
                $"{baseUrl}/probe/time?StartTime={Uri.EscapeDataString("2026-03-15 13:00:00")}");
            response.EnsureSuccessStatusCode();

            var json = JObject.Parse(await response.Content.ReadAsStringAsync());
            var boundTicks = json.Value<long?>("ticks");
            var boundKind = json.Value<int?>("kind");

            Assert.NotNull(boundTicks);
            // 墙钟数值直接取字面 13:00（不再按 UTC 保留），与后端 DateTime.Now 本地入库同源 → 修复成立
            var bound = new DateTime(boundTicks!.Value);
            Assert.Equal(2026, bound.Year);
            Assert.Equal(3, bound.Month);
            Assert.Equal(15, bound.Day);
            Assert.Equal(13, bound.Hour);
            Assert.Equal(0, bound.Minute);
            // 关键：绝不出现 Kind=Utc（那会带着 Z 语义被下游按字段值直绑成本地墙上 05:00 的偏差）
            Assert.NotEqual((int)DateTimeKind.Utc, boundKind!.Value);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}

/// <summary>
/// 真实 MVC 查询绑定探针：复用生产 <see cref="LogQuery"/>（含 <c>DateTime? StartTime</c>），
/// 只回显绑定结果，不触碰任何业务依赖，供基线钉测定时区语义。
/// </summary>
public sealed class TimeBindProbeController : ControllerBase
{
    [HttpGet("probe/time")]
    public IActionResult Get([FromQuery] LogQuery query)
    {
        return Ok(new
        {
            Ticks = query.StartTime?.Ticks,
            Kind = (int?)query.StartTime?.Kind
        });
    }
}
