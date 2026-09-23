using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Quantum.Application;
using Quantum.Entities.Model;
using Quantum.Utils;
using Quantum.Entities.Result;
using Quantum.Web.Filters;

namespace Quantum.API.Tests.Contract;

/// <summary>
/// 契约测试：ExceptionFilter 的错误语义。
/// 业务错误与系统错误都以 HTTP 200 + body Code=500 返回（HTTP 恒 200 契约），
/// BusinessException 的 Message 是面向用户的 UI 文案，前端直接 toast。
/// </summary>
public class ExceptionFilterContractTests
{
    static ExceptionFilterContractTests() => Log4NetTestSetup.EnsureRepository();

    private static ExceptionContext CreateContext(Exception exception)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ExceptionContext(actionContext, [])
        {
            Exception = exception,
            ExceptionHandled = false
        };
    }

    [Fact]
    public void BusinessException_Returns_Code500_With_Message_And_Http200()
    {
        var context = CreateContext(new BusinessException("脚本文件不存在，请检查！"));

        new ExceptionFilter().OnException(context);

        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        var envelope = Assert.IsType<ResultModel>(objectResult.Value);
        Assert.Equal(500, envelope.Code);
        Assert.Equal("脚本文件不存在，请检查！", envelope.Message);
        Assert.Equal(200, context.HttpContext.Response.StatusCode);
    }

    [Fact]
    public void UnhandledException_Returns_Code500_Without_Leaking_Details_In_Release()
    {
        var context = CreateContext(new InvalidOperationException("SQL 连接串：server=secret"));

        new ExceptionFilter().OnException(context);

        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        var envelope = Assert.IsType<ResultModel>(objectResult.Value);
        Assert.Equal(500, envelope.Code);
        Assert.Equal(200, context.HttpContext.Response.StatusCode);
#if !DEBUG
        Assert.DoesNotContain("secret", envelope.Message);
#endif
    }

    [Fact]
    public void UnhandledException_Is_Recorded_Into_Business_Log_Queue()
    {
        var context = CreateContext(new ApplicationException("boom"));

        new ExceptionFilter().OnException(context);

        Assert.Contains(LogServiceHelper.Logs.Snapshot(), log => log.Title == "系统错误" && log.Success == false);
    }

    [Fact]
    public void BusinessException_Does_Not_Pollute_Business_Log_Queue()
    {
        var before = LogServiceHelper.Logs.Count;
        var context = CreateContext(new BusinessException("环境变量名称异常"));

        new ExceptionFilter().OnException(context);

        Assert.Equal(before, LogServiceHelper.Logs.Count);
    }

    [Fact]
    public void UnauthorizedBusinessException_Returns_Code401_And_Http200()
    {
        // 确定性认证拒绝（App 刷新令牌无效/重放）：Code=401 让客户端与临时故障(500)区分——只在 401 时清会话跳登录
        var context = CreateContext(new UnauthorizedBusinessException("刷新令牌无效或已过期，请重新登录！"));

        new ExceptionFilter().OnException(context);

        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        var envelope = Assert.IsType<ResultModel>(objectResult.Value);
        Assert.Equal(401, envelope.Code);
        Assert.Equal("刷新令牌无效或已过期，请重新登录！", envelope.Message);
        Assert.Equal(200, context.HttpContext.Response.StatusCode);
    }
}
