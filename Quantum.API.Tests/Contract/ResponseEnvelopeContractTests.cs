using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Quantum.Entities.Result;

namespace Quantum.API.Tests.Contract;

/// <summary>
/// 契约测试（App 设计文档 A2）：锁定统一响应包的序列化格式。
/// 后端 ResultFilter 将全部返回值包装为 {Code, Message, Data}，
/// 前端 quantum-web/src/libs/axios.js 以 response.data.Code == 200 判定成功，
/// 属性名必须是 Pascal 大小写（Startup.cs 的 Newtonsoft DefaultContractResolver），
/// 一旦被改成 camelCase 或 code 小写，前端全线崩。
/// </summary>
public class ResponseEnvelopeContractTests
{
    /// <summary>
    /// 与 Startup.AddNewtonsoftJson 完全一致的序列化设置。
    /// Startup 调整序列化配置时必须同步修改此处，测试会强制你意识到契约影响。
    /// </summary>
    public static JsonSerializerSettings CreateStartupSerializerSettings() => new()
    {
        ContractResolver = new DefaultContractResolver(),
        DateTimeZoneHandling = DateTimeZoneHandling.Local,
        DateFormatString = "yyyy-MM-dd HH:mm:ss",
        NullValueHandling = NullValueHandling.Include
    };

    [Fact]
    public void Envelope_Properties_Are_Exactly_Code_Message_Data_In_PascalCase()
    {
        var envelope = ResultModel<string>.Success("payload");

        var json = JsonConvert.SerializeObject(envelope, CreateStartupSerializerSettings());
        var obj = JObject.Parse(json);

        var keys = obj.Properties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["Code", "Data", "Message"], keys);
        Assert.Equal(200, (int)obj["Code"]!);
        Assert.Equal("payload", (string)obj["Data"]!);
    }

    [Fact]
    public void Error_Envelope_Keeps_Code500_And_NullData_Included()
    {
        var envelope = ResultModel<string>.Error("业务失败文案");

        var json = JsonConvert.SerializeObject(envelope, CreateStartupSerializerSettings());
        var obj = JObject.Parse(json);

        Assert.Equal(500, (int)obj["Code"]!);
        Assert.Equal("业务失败文案", (string)obj["Message"]!);
        Assert.Equal(JTokenType.Null, obj["Data"]!.Type);
    }

    [Fact]
    public void Envelope_With_Complex_Data_Keeps_Inner_Property_Casing()
    {
        var envelope = new ResultModel<object> { Code = 200, Message = "Success", Data = new { TotalCount = 3, Page = 1 } };

        var json = JsonConvert.SerializeObject(envelope, CreateStartupSerializerSettings());
        var data = (JObject)JObject.Parse(json)["Data"]!;

        Assert.Equal(3, (int)data["TotalCount"]!);
        Assert.Null(data["totalCount"]);
    }

    [Fact]
    public void DateTime_In_Data_Uses_Uniform_Format()
    {
        var envelope = new ResultModel<object> { Code = 200, Message = "Success", Data = new { CreateTime = new DateTime(2026, 9, 12, 8, 30, 5) } };

        var json = JsonConvert.SerializeObject(envelope, CreateStartupSerializerSettings());

        Assert.Contains("\"CreateTime\":\"2026-09-12 08:30:05\"", json);
    }
}
