using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 外部推送凭据管理（G-Push）：创建/轮换/启停/删除与校验。
///
/// 密钥口径：密码学安全随机 ≥256 位，明文只在创建/轮换那一次回显；库里只有 SHA-256 摘要，
/// 校验走固定时间比较。列表、日志、错误文案一律不回密钥或完整 Authorization 头。
/// </summary>
public class ExternalPushCredentialService
{
    /// <summary>新建凭据的默认每分钟配额</summary>
    public const int DefaultRateLimitPerMinute = 60;

    /// <summary>新建凭据的默认每日配额</summary>
    public const int DefaultDailyQuota = 1000;

    private readonly IQuantumDbContext _db;

    public ExternalPushCredentialService(IQuantumDbContext db)
    {
        _db = db;
    }

    /// <summary>创建结果：明文密钥（<see cref="Secret"/>）仅此一次可见。</summary>
    public record Created(ExternalPushCredentialModel Credential, string Secret);

    /// <summary>
    /// 创建凭据。**默认禁用**（Enabled=false）：管理员拿到密钥后自行核对无误再启用，
    /// 避免「密钥已生成但没人负责启用」的裸奔窗口。
    /// </summary>
    public async Task<Created> CreateAsync(string displayName, int rateLimitPerMinute, int dailyQuota,
        string remark, DateTime? expiresAtUtc)
    {
        var name = (displayName ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 80)
        {
            throw new BusinessException("接入方名称需为 1~80 个字符");
        }

        var secret = GenerateSecret();
        var credential = new ExternalPushCredentialModel
        {
            DisplayName = name,
            Enabled = false,
            SecretHash = HashSecret(secret),
            RateLimitPerMinute = Math.Clamp(rateLimitPerMinute <= 0 ? DefaultRateLimitPerMinute : rateLimitPerMinute, 1, 6000),
            DailyQuota = Math.Clamp(dailyQuota <= 0 ? DefaultDailyQuota : dailyQuota, 1, 1_000_000),
            Remark = (remark ?? string.Empty).Trim(),
            ExpiresAtUtc = expiresAtUtc
        };
        _db.ExternalPushCredentials.Add(credential);
        await _db.SaveChangesAsync();
        LogServiceHelper.Info("创建外部推送凭据", $"接入方={credential.DisplayName}，Id={credential.Id}（默认禁用）",
            "ExternalPush");
        return new Created(credential, secret);
    }

    /// <summary>轮换密钥：返回新的明文一次；旧密钥立即失效（摘要被覆盖）。</summary>
    public async Task<string> RotateAsync(string id)
    {
        var credential = await _db.ExternalPushCredentials.FirstOrDefaultAsync(n => n.Id == id);
        if (credential == null)
        {
            throw new BusinessException("凭据不存在");
        }

        var secret = GenerateSecret();
        credential.SecretHash = HashSecret(secret);
        credential.RevokedAtUtc = null;
        await _db.SaveChangesAsync();
        LogServiceHelper.Warn("轮换外部推送凭据密钥", $"接入方={credential.DisplayName}，Id={credential.Id}",
            "ExternalPush");
        return secret;
    }

    /// <summary>启用/禁用（禁用即吊销语义：停止新投递，但不抹已存消息）。</summary>
    public async Task<bool> SetEnabledAsync(string id, bool enabled, string @operator)
    {
        var credential = await _db.ExternalPushCredentials.FirstOrDefaultAsync(n => n.Id == id);
        if (credential == null)
        {
            throw new BusinessException("凭据不存在");
        }

        credential.Enabled = enabled;
        credential.RevokedAtUtc = enabled ? null : DateTime.UtcNow;
        await _db.SaveChangesAsync();
        LogServiceHelper.Info(enabled ? "启用外部推送凭据" : "禁用外部推送凭据",
            $"接入方={credential.DisplayName}，操作人={@operator}", "ExternalPush");
        return true;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var credential = await _db.ExternalPushCredentials.FirstOrDefaultAsync(n => n.Id == id);
        if (credential == null)
        {
            return true;
        }

        _db.ExternalPushCredentials.Remove(credential);
        // 幂等记录一并删除：凭据没了，键去重也就失去意义（已存通知与会话消息保留）
        var requests = await _db.ExternalPushRequests.Where(n => n.CredentialId == id).ToListAsync();
        if (requests.Count > 0)
        {
            _db.ExternalPushRequests.RemoveRange(requests);
        }
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>列表元数据（不含密钥、不含摘要）。</summary>
    public async Task<List<ExternalPushCredentialModel>> ListAsync()
        => await _db.ExternalPushCredentials.AsNoTracking().OrderBy(n => n.CreatedAtUtc).ToListAsync();

    /// <summary>
    /// 校验 <c>Authorization: PushKey {id}.{secret}</c>。
    /// 任何失败（无头/格式错/Id 不存在/已禁用/已过期/密钥不符）都返回 null——**不区分原因**，
    /// 否则调用方可以用它探测凭据 Id 是否存在。
    /// </summary>
    public async Task<ExternalPushCredentialModel> VerifyAsync(string authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        const string scheme = "PushKey ";
        if (!authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var payload = authorizationHeader[scheme.Length..].Trim();
        var dot = payload.IndexOf('.');
        if (dot <= 0 || dot == payload.Length - 1)
        {
            return null;
        }

        var id = payload[..dot];
        var secret = payload[(dot + 1)..];
        if (id.Length is < 8 or > 64 || secret.Length is < 32 or > 256)
        {
            return null;
        }

        var credential = await _db.ExternalPushCredentials.FirstOrDefaultAsync(n => n.Id == id);
        if (credential == null || !credential.Enabled)
        {
            // 常量时间等价耗时：不存在的 Id 也做一次摘要计算，避免时序侧信道区分
            CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
                SHA256.HashData(Encoding.UTF8.GetBytes(credential?.SecretHash ?? "x")));
            return null;
        }

        if (credential.ExpiresAtUtc != null && credential.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
                Convert.FromHexString(credential.SecretHash)))
        {
            return null;
        }

        return credential;
    }

    /// <summary>生成 ≥256 位随机密钥（Base64Url，无填充）。</summary>
    private static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashSecret(string secret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
