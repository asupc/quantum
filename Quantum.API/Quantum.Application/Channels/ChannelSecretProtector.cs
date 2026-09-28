using System.Security.Cryptography;
using System.Text;
using Quantum.Utils;

namespace Quantum.Application.Channels;

/// <summary>平台密文独立主密钥；优先级：密钥文件 > 环境变量 > appsettings.json Quantum 节（为空时启动已自动生成），均不可用时通道默认不可配置/运行。</summary>
public sealed class ChannelSecretProtector
{
    private readonly byte[] _key;
    private readonly string _availabilityError;

    public ChannelSecretProtector()
    {
        // 错误配置不影响原 App 服务启动；只有启用通道的管理请求会收到脱敏错误。
        var file = Environment.GetEnvironmentVariable("QUANTUM_CHANNEL_KEY_FILE");
        string encoded;
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!Path.IsPathFullyQualified(file))
            {
                _availabilityError = "通道密钥文件必须使用绝对路径";
                return;
            }
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 128)
                {
                    _availabilityError = "通道密钥文件格式无效";
                    return;
                }
                using var reader = new StreamReader(stream);
                encoded = reader.ReadToEnd().Trim();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _availabilityError = "无法读取通道主密钥文件";
                return;
            }
        }
        else
        {
            encoded = Environment.GetEnvironmentVariable("QUANTUM_CHANNEL_MASTER_KEY");
            if (string.IsNullOrWhiteSpace(encoded))
                encoded = SystemConfigHelper.GetSetting().ChannelMasterKey;
        }
        if (encoded.Length == 0) return;
        try { _key = Convert.FromBase64String(encoded); }
        catch (FormatException)
        {
            _availabilityError = "通道主密钥必须为 32 字节的 Base64 编码";
            return;
        }
        if (_key.Length != 32)
        {
            _key = null;
            _availabilityError = "通道主密钥必须为 32 字节的 Base64 编码";
        }
    }

    internal ChannelSecretProtector(byte[] key)
    {
        if (key is null || key.Length != 32) throw new ArgumentException("密钥长度须为 32 字节", nameof(key));
        _key = (byte[])key.Clone();
    }

    public bool Available => _key is { Length: 32 };

    /// <summary>不可用时的具体原因（可用时为 null）；供管理端守卫把根因透给操作者。</summary>
    public string AvailabilityError => _availabilityError;

    private byte[] Key => Available ? _key : throw new BusinessException(
        _availabilityError ?? "通道主密钥未配置且自动生成失败");

    public string Protect(string value, string accountId, string purpose)
    {
        if (string.IsNullOrEmpty(value)) throw new BusinessException("通道凭据不能为空");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var bytes = Encoding.UTF8.GetBytes(value);
        var encrypted = new byte[bytes.Length];
        try
        {
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(nonce, bytes, encrypted, tag, Encoding.UTF8.GetBytes(accountId + ":" + purpose));
            return "v1:" + Convert.ToBase64String([.. nonce, .. tag, .. encrypted]);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string Unprotect(string ciphertext, string accountId, string purpose)
    {
        try
        {
            if (ciphertext is null || !ciphertext.StartsWith("v1:", StringComparison.Ordinal))
                throw new CryptographicException();
            var bytes = Convert.FromBase64String(ciphertext[3..]);
            if (bytes.Length < 29 || bytes.Length > 131072) throw new CryptographicException();
            var plain = new byte[bytes.Length - 28];
            try
            {
                using var aes = new AesGcm(Key, 16);
                aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain,
                    Encoding.UTF8.GetBytes(accountId + ":" + purpose));
                return Encoding.UTF8.GetString(plain);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            throw new BusinessException("通道凭据无法解密，请核对独立主密钥或恢复备份");
        }
    }

    public string HashChallenge(string accountId, string code)
        => Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes("challenge:" + accountId + ":" + code)));

    public bool VerifyChallenge(string accountId, string code, string expected)
    {
        if (string.IsNullOrEmpty(expected) || expected.Length != 64) return false;
        try
        {
            var actual = HashChallenge(accountId, code);
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected));
        }
        catch (FormatException) { return false; }
    }
}
