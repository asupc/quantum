using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Quantum.Entities.Result;

namespace Quantum.Web.Filters;

public class ResultFilter : ResultFilterAttribute
{
    public override void OnResultExecuting(ResultExecutingContext context)
    {
        // 如果返回结果已经是ResultModel类型，则不进行额外处理

        if (context.Result is StatusCodeResult statusCodeResult)
        {
            // 处理无返回值的操作结果
            var resultModel = new ResultModel
            {
                Code = statusCodeResult.StatusCode
            };

            context.Result = new ObjectResult(resultModel)
            {
                StatusCode = 200
            };
        }
        else if (context.Result is EmptyResult)
        {
            // 处理空结果
            var resultModel = new ResultModel
            {
                Code = 200,
                Message = "Success"
            };

            context.Result = new ObjectResult(resultModel)
            {
                StatusCode = 200
            };
        }
        else if (context.Result is ObjectResult objectResult)
        {
            // 检查是否已经是ResultModel或ResultModel<T>类型
            var resultType = objectResult.Value?.GetType();
            if (resultType != null && (resultType == typeof(ResultModel) || IsResultModelType(resultType)))
            {
                base.OnResultExecuting(context);
                return;
            }
            objectResult.Value = objectResult.Value;
            objectResult.StatusCode = 200;
            var resultModel = new ResultModel<object>
            {
                Code = 200,
                Message = "Success",
                Data = objectResult.Value
            };

            context.Result = new ObjectResult(resultModel)
            {
                StatusCode = 200
            };
        }
        base.OnResultExecuting(context);
    }

    private bool IsResultModelType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ResultModel<>))
            return true;

        if (type.BaseType != null)
            return IsResultModelType(type.BaseType);

        return false;
    }
}