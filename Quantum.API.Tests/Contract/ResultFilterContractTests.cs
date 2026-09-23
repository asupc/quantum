using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Quantum.Entities.Model;
using Quantum.Entities.Result;
using Quantum.Web.Filters;

namespace Quantum.API.Tests.Contract;

/// <summary>
/// 契约测试：ResultFilter 的包装行为。
/// 前端拦截器解包后只拿 Data，非 200 时 toast Message——过滤器行为变化即契约变化。
/// </summary>
public class ResultFilterContractTests
{
    private static ResultExecutingContext CreateContext(ActionResult result)
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ResultExecutingContext(actionContext, [], result, new object());
    }

    private static T Execute<T>(ActionResult result) where T : class
    {
        var context = CreateContext(result);
        new ResultFilter().OnResultExecuting(context);
        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(200, objectResult.StatusCode);
        return Assert.IsType<T>(objectResult.Value);
    }

    [Fact]
    public void Raw_Object_Result_Is_Wrapped_With_Code200()
    {
        var envelope = Execute<ResultModel<object>>(new ObjectResult(new { any = "value" }));

        Assert.Equal(200, envelope.Code);
        Assert.Equal("Success", envelope.Message);
        Assert.NotNull(envelope.Data);
    }

    [Fact]
    public void Already_Enveloped_Result_Is_Not_Double_Wrapped()
    {
        var context = CreateContext(new ObjectResult(ResultModel<string>.Success("x")));
        new ResultFilter().OnResultExecuting(context);

        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        var value = Assert.IsType<ResultModel<string>>(objectResult.Value);
        Assert.Equal("x", value.Data);
    }

    [Fact]
    public void StatusCodeResult_Is_Converted_To_Body_Code_With_Http200()
    {
        var envelope = Execute<ResultModel>(new StatusCodeResult(404));

        Assert.Equal(404, envelope.Code);
    }

    [Fact]
    public void EmptyResult_Is_Converted_To_Code200_Success()
    {
        var envelope = Execute<ResultModel>(new EmptyResult());

        Assert.Equal(200, envelope.Code);
        Assert.Equal("Success", envelope.Message);
    }

    [Fact]
    public void Entity_Returned_From_Action_Is_Wrapped_Not_Rejected()
    {
        // 控制器直接返回领域实体是存量普遍写法，契约上仍会被包装成 {Code,Message,Data}
        var task = new TaskModel { Id = "T1", Name = "demo" };
        var context = CreateContext(new ObjectResult(task));
        new ResultFilter().OnResultExecuting(context);

        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        var envelope = Assert.IsType<ResultModel<object>>(objectResult.Value);
        Assert.Same(task, envelope.Data);
    }
}
