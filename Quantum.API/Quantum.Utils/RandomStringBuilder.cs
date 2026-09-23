using System.Security.Cryptography;
using System.Text;

namespace Quantum.Utils;

/// <summary>
/// 安全随机字符串（CSPRNG）：用于 JWT 密钥、管理员初始口令、App 刷新令牌、重置口令等
/// 安全敏感值。原实现逐字符 new Random(Guid.GetHashCode())，非密码学随机且种子熵不足，
/// 30 天刷新令牌可被预测，已整体替换为 RandomNumberGenerator。
/// </summary>
public static class RandomStringBuilder
{
    private const string Charset = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// 生成随机字符串（大小写字母 + 数字，密码学安全）。
    /// </summary>
    /// <param name="length">字符串的长度</param>
    public static string Create(int length = 32)
    {
        if (length <= 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            sb.Append(Charset[RandomNumberGenerator.GetInt32(Charset.Length)]);
        }
        return sb.ToString();
    }
}
