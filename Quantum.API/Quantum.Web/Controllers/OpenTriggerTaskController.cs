using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;
using Quantum.Web.Filters;

namespace Quantum.Web.Controllers;

/// <summary>
/// 外触内执
/// </summary>
[Route("api/[controller]")]
[CustomAuthorizationFilter]
// 配置的增删改查/启停全部管理员专用：配置即执行权（SrciptFile 指向 scripts/quantum 内任意脚本，
// 配合匿名 Trigger 端点可反复触发执行），与 Task 脚本链路同级敏感，普通 App 用户/Open 匿名令牌不得可达。
// 匿名触发端点（GET/POST {secret}）标有 [AllowAnonymous]，不受本过滤器影响。
[LoggedInUser]
public class OpenTriggerTaskController : BaseController
{
    OpenTriggerTaskService openTriggerTaskService;

    public OpenTriggerTaskController(OpenTriggerTaskService openTriggerTaskService)
    {
        this.openTriggerTaskService = openTriggerTaskService;
    }

    /// <summary>
    /// 触发执行（GET：查询参数即载荷）
    /// </summary>
    /// <param name="secret">配置安全密钥</param>
    /// <returns></returns>
    [HttpGet("{secret}"), AllowAnonymous]
    public string TriggerByGet([FromRoute] string secret)
    {
        var ip = GetUserIp();
        var data = HttpContext.Request.Query.ToList();

        Dictionary<string, string> dic = [];
        foreach (var item in data)
        {
            dic.Add(item.Key, item.Value.FirstOrDefault());
        }
        return openTriggerTaskService.Trigger(secret, "GET", ip, dic);
    }

    /// <summary>
    /// 触发执行（POST：请求体自行解析，不走 MVC 绑定）
    /// </summary>
    /// <param name="secret">配置安全密钥</param>
    /// <returns></returns>
    // 为什么手动读 body 而不是 [FromBody]：MVC 的 Newtonsoft 输入格式器内部固定 JsonTextReader.MaxDepth=32，
    // 且本项目 SuppressModelStateInvalidFilter=true——深于 32 层的请求体会**静默绑成 null**：
    // 端点照旧返回「执行成功」、运行记录记成功，而载荷被悄悄丢弃（实测 depth 33/40/64/1000 全部如此）。
    // 换参数类型（object → JToken）无用：深度上限在格式器里，与目标类型无关。
    // 因此这里显式读流 + JToken.Parse（迭代式解析、无深度上限），把「该不该拒绝」交给服务侧 ValidatePayload。
    [HttpPost("{secret}"), AllowAnonymous]
    public async Task<string> TriggerByPost([FromRoute] string secret)
    {
        var data = await ReadBodyAsync();
        return openTriggerTaskService.Trigger(secret, "POST", GetUserIp(), data);
    }

    /// <summary>
    /// 读取请求体为 JToken：空体、JSON null 或非法 JSON 一律拒绝，绝不静默降级成 null——
    /// 静默 null 正是「载荷被丢弃但仍报告成功」的根因，宁可明确报错。
    /// </summary>
    private async Task<JToken> ReadBodyAsync()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new BusinessException("请求体不能为空！");
        }
        JToken token;
        try
        {
            // 必须显式放开读取深度：JToken.Parse / 默认反序列化的读取侧 MaxDepth=64，
            // 深体在那层就抛 JsonReaderException，会被下面的 catch 误报成「不是合法 JSON」，
            // 且真正的深度判定永远轮不到 ValidatePayload。放开深度是安全的——JToken 解析是迭代式的
            // （已实测 10 万层不爆栈），拒绝与否由服务侧的显式上限决定。
            token = JsonConvert.DeserializeObject<JToken>(raw, new JsonSerializerSettings { MaxDepth = null });
        }
        catch (JsonException)
        {
            throw new BusinessException("请求体不是合法 JSON！");
        }
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
        {
            throw new BusinessException("请求体不能为空！");
        }
        return token;
    }

    /// <summary>
    /// 添加或者更新外触内执配置
    /// </summary>
    [HttpPost]
    [ActionLogFilter("添加或者更新外触内执配置")]
    public Task<bool> AddAsync([FromBody] OpenTriggerTask openTriggerTask)
    {
        return openTriggerTaskService.AddAsync(openTriggerTask);
    }

    /// <summary>
    /// 删除外触内执配置（ids 逗号分隔，与 Task 批量删除约定一致）
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [ActionLogFilter("删除外触内执配置")]
    [HttpDelete]
    public Task<bool> DeleteAsync([FromQuery] string ids)
    {
        return openTriggerTaskService.DeleteAsync(ids);
    }

    /// <summary>
    /// 获取外触内执配置信息
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public Task<PageResult<OpenTriggerTask>> GetAsync([FromQuery] BaseQuery baseQuery)
    {
        return openTriggerTaskService.GetAsync(baseQuery);
    }


    /// <summary>
    /// 批量禁用外触内执指令
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [ActionLogFilter("批量禁用外触内执指令")]
    [HttpPut("disable")]
    public Task DisableAsync([FromBody] List<string> ids)
    {
        return openTriggerTaskService.DisableAsync(ids);
    }

    /// <summary>
    /// 批量启用外触内执指令
    /// </summary>
    /// <param name="ids"></param>
    /// <returns></returns>
    [ActionLogFilter("批量启用外触内执指令")]
    [HttpPut("enable")]
    public Task EnableAsync([FromBody] List<string> ids)
    {
        return openTriggerTaskService.EnableAsync(ids);
    }
}
