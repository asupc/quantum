using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Filters;
using Quantum.Application;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Web.Filters;

/// <summary>
/// 操作日志过滤器。作为特性标注在 action 上时，MVC 会缓存并跨请求复用同一实例，
/// 因此计时器绝不能放实例字段（并发请求会互相 Stop/覆写）——改存 HttpContext.Items，每请求独立。
/// </summary>
public class ActionLogFilter : ActionFilterAttribute
{
    private const string TimingKey = "__ActionLogStopwatch";

    private readonly string title;
    private readonly string remark;

    public ActionLogFilter(string title, string remark = "")
    {
        this.title = title;
        this.remark = remark;
    }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        context.HttpContext.Items[TimingKey] = Stopwatch.StartNew();
        await next();
    }

    public override async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var executed = await next();
        var success = executed.Exception == null;
        var stopwatch = context.HttpContext.Items[TimingKey] as Stopwatch;
        LogServiceHelper.Logs.Enqueue(new LogModel
        {
            CreateTime = DateTime.Now,
            LogType = LogType.操作日志,
            Operator = context.HttpContext.GetUserId(),
            Remark = remark,
            Success = success,
            UserIP = context.HttpContext.GetUserIp(),
            Title = title,
            Severity = success ? LogSeverity.Info : LogSeverity.Error,
            Module = "Action",
            RequestPath = context.HttpContext.Request.Path,
            ElapsedMs = stopwatch?.ElapsedMilliseconds ?? 0
        });
    }
}
