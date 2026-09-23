using Quantum.Application;

namespace Quantum.API.Tests;

/// <summary>
/// EnumService 回归（多项目分层遗留问题，2026-09-16）：
/// 原实现硬编码反射 Quantum.dll——分层后枚举集中在 Quantum.Entities 等程序集，
/// 主程序集已无枚举，/api/Enum 返回空对象，前端日志中心等枚举列渲染失败（表体空白）。
/// </summary>
public class EnumServiceTests
{
    [Fact]
    public void Enums_ContainsEntitiesAssemblyEnums_AfterLayeredSplit()
    {
        var enums = new EnumService().Enums();
        Assert.NotEmpty(enums);

        // 日志中心列渲染直接依赖 LogType；通信/消息类型为 App 通道契约依赖
        Assert.True(enums.ContainsKey("LogType"), "缺少 LogType：枚举扫描未覆盖 Quantum.Entities");
        Assert.NotEmpty(enums["LogType"]);
        Assert.Contains(enums["LogType"], n => n.Key == "任务日志");

        Assert.True(enums.ContainsKey("CommunicationType"), "缺少 CommunicationType");
        Assert.True(enums.ContainsKey("MessageType"), "缺少 MessageType");
    }
}
