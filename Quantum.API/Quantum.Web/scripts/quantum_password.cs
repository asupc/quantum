// ============================================================================
// 随机密码（由 quantum_passwod.js 转换，2026-09-16）：
//   生成一个随机密码；指令带 -1 则不保存数据库（如「随机密码-1」），否则存入自定义数据。
// 迁移映射（计划 2.7）：sendNotify→ctx.Notify；addCustomData→ctx.CustomData；
//   uuid(18,null,"!@#$%^&*()-=_+,.;':")→RandomNumberGenerator 同字符集采样；process.env→ctx.Variables。
// ============================================================================
using System.Security.Cryptography;
using Quantum.Plugins;

public class RandomPasswordTask : IQuantumTask
{
    private const string CustomDataType = "quantum_password";

    // 与旧版 uuid 的字符集一致：字母数字 + 特殊字符
    private const string PasswordChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!@#$%^&*()-=_+,.;':";

    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        // 指令消息（MessageProcess 注入 command 变量；任务编辑页可自定义指令变量名）
        var command = ctx.Variables.TryGetValue("command", out var c) ? c : "";
        var userId = ctx.Variables.TryGetValue("CommunicationUserId", out var uid) ? uid : null;
        var userName = ctx.Variables.TryGetValue("CommunicationUserName", out var uname) ? uname : null;

        // 加密安全随机（旧版 Math.random 采样的等价增强）
        var password = new char[18];
        for (var i = 0; i < password.Length; i++)
        {
            password[i] = PasswordChars[RandomNumberGenerator.GetInt32(PasswordChars.Length)];
        }
        var generated = new string(password);
        ctx.Log("生成密码：" + generated);

        if (command.Contains("-1"))
        {
            await ctx.Notify.SendAsync("随机密码", "该密码不会被保存，请妥善保管。", ct);
        }
        else
        {
            await ctx.CustomData.AddAsync(
            [
                new QuantumCustomDataValue
                {
                    Type = CustomDataType,
                    Data1 = command.Replace("随机密码", ""),
                    Data2 = generated,
                    Data3 = command,
                    Data4 = userId,
                    Data5 = userName
                }
            ], ct);
        }
        await ctx.Notify.SendAsync("随机密码", generated, ct);
    }
}
