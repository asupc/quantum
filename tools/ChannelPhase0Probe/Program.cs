using System.Security.Cryptography;

if (args.Length == 1 && args[0] == "selftest")
{
    return ProbeSelfTest.Run();
}
if (args.Length != 1 || args[0] is not ("qq" or "qq-capture" or "qq-passive" or "weixin" or "qq-active" or "weixin-active"))
{
    Console.Error.WriteLine("用法：dotnet run --project tools/ChannelPhase0Probe -- qq|qq-capture|qq-passive|weixin|qq-active|weixin-active|selftest");
    return 2;
}
if (Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Console.Error.WriteLine("仅允许交互式私密终端；拒绝将二维码、身份和结果重定向到日志。");
    return 2;
}
if (args[0].EndsWith("-active", StringComparison.Ordinal) || args[0] == "qq-passive")
{
    var windowTest = args[0] == "qq-passive";
    var gate = windowTest ? "P0_ENABLE_WINDOW_EXPERIMENT" : "P0_ENABLE_ACTIVE_EXPERIMENT";
    if (Environment.GetEnvironmentVariable(gate) != "YES")
    {
        Console.Error.WriteLine($"试验未启用；仅测试账号可在本机设置 {gate}=YES。");
        return 2;
    }
    Console.Write(windowTest
        ? "本操作将用手工提供的旧 msg_id 尝试被动回复指定测试账号。输入 SEND-TEST 确认："
        : "本操作可能给指定测试账号发送一条无入站上下文的消息。输入 SEND-TEST 确认：");
    if (Console.ReadLine() != "SEND-TEST") return 2;
}
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var challenge = "Q-P0-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
Console.WriteLine($"本次测试口令：{challenge}（只能由测试用户私聊机器人发送；不要转发或截图）。");
try
{
    switch (args[0])
    {
        case "qq": await QqProbe.RunAsync(challenge, shutdown.Token); break;
        case "qq-capture": await QqProbe.RunAsync(challenge, shutdown.Token, captureOnly: true); break;
        case "qq-passive": await QqProbe.RunPassiveAsync(challenge, shutdown.Token); break;
        case "weixin": await WeixinProbe.RunAsync(challenge, shutdown.Token); break;
        case "qq-active": await QqProbe.RunActiveAsync(challenge, shutdown.Token); break;
        case "weixin-active": await WeixinProbe.RunActiveAsync(challenge, shutdown.Token); break;
    }
    return 0;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    Console.WriteLine("已停止；尚未验证完整 P0 门禁。");
    return 0;
}
catch (Exception e)
{
    // HttpRequestException/WebSocketException 可能包含 URL/响应详情，只打印异常类别；业务错误只打印预设脱敏信息。
    Console.Error.WriteLine(e is ProbeException ? e.Message : $"测试失败：{e.GetType().Name}（详情不输出，避免泄露身份/令牌）。");
    return 1;
}
