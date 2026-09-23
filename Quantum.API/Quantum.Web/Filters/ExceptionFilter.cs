using log4net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Quantum.Application;
using Quantum.Entities.Model;
using Quantum.Entities.Result;
using Quantum.Utils;

namespace Quantum.Web.Filters;

public class ExceptionFilter : IExceptionFilter
{
    private readonly ILog log = LogManager.GetLogger("NETCoreRepository", typeof(ExceptionFilter));

    public void OnException(ExceptionContext context)
    {

        if (context.Exception is BusinessException businessException)
        {
            context.Result = new ObjectResult(
                new ResultModel
                {
                    // UnauthorizedBusinessException = 确定性认证拒绝（如 App 刷新令牌无效/重放），
                    // 与普通业务失败(500)区分开：客户端只在 401 时清会话跳登录，500 视为临时故障可重试
                    Code = businessException is UnauthorizedBusinessException ? 401 : 500,
                    Message = businessException.Message
                });
            context.HttpContext.Response.StatusCode = 200;
            return;
        }

        LogServiceHelper.Logs.Enqueue(new LogModel
        {
            CreateTime = DateTime.Now,
            LogType = LogType.系统日志,
            Operator = "System",
            Remark = context.Exception.Message,
            Success = false,
            UserIP = context.HttpContext.GetUserIp(),
            Title = "系统错误",
            Severity = LogSeverity.Error,
            Module = "System",
            RequestPath = context.HttpContext.Request.Path,
            Exception = context.Exception.ToString()
        });
        log.Error(context.HttpContext.Request.Path + context.HttpContext.Request.QueryString, context.Exception);
        context.Result = new ObjectResult(
            new ResultModel
            {
                Code = 500,
#if DEBUG
                Message = context.Exception.ToString()
#endif
#if !DEBUG
                Message = "系统出了点问题，开发者也有点懵逼，请将日志中心的错误日志发送给小菜鸡。"
#endif
            });
        context.HttpContext.Response.StatusCode = 200;
    }
}
