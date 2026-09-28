using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// 仅供隔离的 P0 探针测试重启；生产通道必须改用 DB Inbox/Outbox 原子事务及正式密钥管理。
internal sealed record WeixinProbeState(
    string BotId, string PeerId, string BotToken, string ApiBase, string Cursor, string[] AttemptedMessageIds);

internal sealed class WeixinStateStorage : IDisposable
{
    private static readonly byte[] Prefix = "QCP0WX1"u8.ToArray();
    private static readonly byte[] AssociatedData = "Quantum.ChannelPhase0.Weixin.State.v1"u8.ToArray();
    private readonly byte[] key;
    private readonly string path;
    private readonly FileStream exclusiveLock;

    private WeixinStateStorage(string path, byte[] key)
    {
        this.path = path;
        this.key = key;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        exclusiveLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path + ".lock", UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    internal static WeixinStateStorage? OpenFromEnvironment()
    {
        var encoded = Probe.Optional("P0_WEIXIN_STATE_KEY");
        if (encoded is null) return null;
        byte[] key;
        try { key = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new ProbeException("P0_WEIXIN_STATE_KEY 必须是 32 字节随机密钥的 Base64 编码。"); }
        if (key.Length != 32) throw new ProbeException("P0_WEIXIN_STATE_KEY 必须是 32 字节随机密钥的 Base64 编码。");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) throw new ProbeException("无法确定用户私有目录，拒绝存储测试登录态。");
        return new WeixinStateStorage(Path.Combine(appData, "Quantum", "ChannelPhase0Probe", "weixin.state"), key);
    }

    // 自检路径必须为系统临时目录中的单文件，不触碰用户真实状态。
    internal static WeixinStateStorage ForSelfTest(string path, byte[] key) => new(path, key);

    internal WeixinProbeState? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            var content = File.ReadAllBytes(path);
            if (content.Length < Prefix.Length + 12 + 16 + 2 || content.Length > 128 * 1024 ||
                !content.AsSpan(0, Prefix.Length).SequenceEqual(Prefix))
                throw new ProbeException("测试登录态格式错误；未自动扫码覆盖已有文件。");
            var offset = Prefix.Length;
            var nonce = content.AsSpan(offset, 12);
            var tag = content.AsSpan(offset + 12, 16);
            var ciphertext = content.AsSpan(offset + 28);
            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
                var state = JsonSerializer.Deserialize<WeixinProbeState>(plaintext, Probe.Json);
                if (state is null || string.IsNullOrWhiteSpace(state.BotId) || string.IsNullOrWhiteSpace(state.PeerId) ||
                    string.IsNullOrWhiteSpace(state.BotToken) || state.AttemptedMessageIds is null ||
                    state.AttemptedMessageIds.Length > 2048 || string.IsNullOrWhiteSpace(state.ApiBase) || state.Cursor is null ||
                    state.AttemptedMessageIds.Any(string.IsNullOrWhiteSpace))
                    throw new ProbeException("测试登录态字段无效；未自动扫码覆盖已有文件。");
                return state;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (CryptographicException)
        {
            throw new ProbeException("测试登录态解密失败；密钥不匹配或文件被篡改，未自动扫码覆盖已有文件。");
        }
        catch (JsonException)
        {
            throw new ProbeException("测试登录态 JSON 无效；未自动扫码覆盖已有文件。");
        }
    }

    internal void Save(WeixinProbeState state)
    {
        if (state.AttemptedMessageIds.Length > 2048) throw new ProbeException("P0 消息历史已满，停止轮询而非遗忘旧的发送尝试。");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, Probe.Json);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
            var content = new byte[Prefix.Length + nonce.Length + tag.Length + ciphertext.Length];
            Prefix.CopyTo(content, 0);
            nonce.CopyTo(content, Prefix.Length);
            tag.CopyTo(content, Prefix.Length + nonce.Length);
            ciphertext.CopyTo(content, Prefix.Length + nonce.Length + tag.Length);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    Options = FileOptions.WriteThrough
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var output = new FileStream(temporary, options))
                {
                    output.Write(content);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true); // 原子替换；失败则保留旧游标与尝试记录。
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                CryptographicOperations.ZeroMemory(content);
            }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Dispose()
    {
        exclusiveLock.Dispose();
        CryptographicOperations.ZeroMemory(key);
    }
}
