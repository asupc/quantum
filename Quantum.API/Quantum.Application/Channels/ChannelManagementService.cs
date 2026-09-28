using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application.Channels;

/// <summary>通道账户/唯一用户绑定管理。外部消息不能取得 Manager 身份，只有 Web Manager 能更改绑定。</summary>
public sealed class ChannelManagementService
{
    public const string Qq = "QQBot";
    public const string Weixin = "WeixinBot";
    public const string Feishu = "FeishuBot";

    private static readonly string[] AllPlatforms = [Qq, Weixin, Feishu];

    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.Ordinal)
    {
        [Qq] = "QQ 机器人", [Weixin] = "微信机器人", [Feishu] = "飞书机器人"
    };

    private readonly IQuantumDbContext _db;
    private readonly ChannelSecretProtector _secret;
    private readonly ChannelAccountGate _gate;

    public ChannelManagementService(IQuantumDbContext db, ChannelSecretProtector secret, ChannelAccountGate gate = null)
    {
        _db = db;
        _secret = secret;
        _gate = gate ?? new ChannelAccountGate();
    }

    public static bool IsSupported(string platform) => AllPlatforms.Contains(platform, StringComparer.Ordinal);

    public static string DisplayName(string platform)
        => DisplayNames.TryGetValue(platform, out var name) ? name : platform;

    public static string RequirePlatform(string platform) => IsSupported(platform)
        ? platform
        : throw new BusinessException("只支持 QQBot、WeixinBot 或 FeishuBot 私聊通道");

    /// <summary>
    /// 原路回复窗口按平台能力分档，创建路由与投递校验必须共用本函数（两处各写一份迟早会漂移）：
    /// 飞书是按 open_id 的主动推送、没有"被动回复窗口"这一说，故放宽到 24 小时，长任务不至于静默丢结果；
    /// 微信受 <c>context_token</c> 约束、QQ 受被动回复窗口与四条上限约束，两者真实窗口均未实测，保持保守 5 分钟。
    /// </summary>
    public static TimeSpan ReplyWindow(string platform) => platform == Feishu
        ? TimeSpan.FromHours(24)
        : TimeSpan.FromMinutes(5);

    public async Task<List<ChannelStatusDto>> StatusAsync()
    {
        var accounts = await _db.ChannelAccounts.AsNoTracking().ToListAsync();
        var pending = await _db.ChannelOutboxes.AsNoTracking()
            .Where(x => x.Status == "Pending" || x.Status == "Retry" || x.Status == "Sending")
            .GroupBy(x => x.AccountId).Select(x => new { AccountId = x.Key, Count = x.Count() }).ToListAsync();
        return AllPlatforms.Select(platform =>
        {
            var account = accounts.FirstOrDefault(x => x.Platform == platform);
            return new ChannelStatusDto
            {
                Platform = platform,
                Configured = account?.CredentialCiphertext is { Length: > 0 },
                Enabled = account?.Enabled ?? false,
                BotIdMasked = Mask(account?.BotId),
                BoundPeerMasked = null,
                CandidatePeerMasked = null,
                CandidateFingerprint = null,
                HasActiveBinding = false, // 旧 DTO 字段仅做兼容；单用户模式不再绑定私聊者。
                ChallengeExpiresAtUtc = account?.ChallengeExpiresAtUtc,
                LastErrorCode = account?.LastErrorCode,
                Environment = platform == Qq ? QqEnvironment(account) : null,
                PendingOutboxCount = account is null ? 0 : pending.FirstOrDefault(x => x.AccountId == account.Id)?.Count ?? 0
            };
        }).ToList();
    }

    public async Task<List<ChannelDeliveryDto>> RecentDeliveryAsync(string platform)
    {
        platform = RequirePlatform(platform);
        var account = await _db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Platform == platform);
        if (account is null) return [];
        var rows = await _db.ChannelOutboxes.AsNoTracking().Where(x => x.AccountId == account.Id)
            .OrderByDescending(x => x.CreatedAtUtc).Take(50).ToListAsync();
        return rows.Select(x => new ChannelDeliveryDto
        {
            Id = x.Id, Platform = platform, Status = x.Status, Purpose = x.Purpose,
            ErrorCode = x.LastErrorCode, Attempts = x.Attempts,
            CreatedAtUtc = x.CreatedAtUtc, AcceptedAtUtc = x.AcceptedAtUtc
        }).ToList();
    }

    /// <summary>
    /// 手动发送一条自检消息，用于「扫码/配置完成但手机侧没动静」时判断断点在收发哪一环。
    ///
    /// 安全约束：目标**只能**取该平台最近一条仍在有效期内的原路回复路由，绝不接受调用方传入收件人——
    /// 否则这个 Manager 接口就成了任意收件人投递器。三家平台（QQ 被动回复、微信 context_token、
    /// 飞书按 open_id 回复）都只能凭入站消息原路返回，凭据本身不支持主动外发，故无路由时给出可执行的下一步。
    /// </summary>
    public async Task<ChannelDeliveryDto> TestSendAsync(string platform)
    {
        platform = RequirePlatform(platform);
        var account = await _db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x =>
            x.Platform == platform && x.Enabled && x.CredentialCiphertext != null);
        if (account is null) throw new BusinessException("该通道尚未接入或未启用，请先完成配置或扫码");
        var route = await _db.ChannelReplyRoutes.AsNoTracking()
            .Where(x => x.AccountId == account.Id && x.ExpiresAtUtc > DateTime.UtcNow)
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync();
        if (route is null)
            throw new BusinessException("没有可回复的私聊对象：请先用手机给机器人发一条消息（本平台只支持按入站消息原路回复，不能主动外发）");
        var probe = $"Quantum 通道自检 {DateTime.Now:HH:mm:ss}：收到本条即表示入站与出站链路都可用";
        // 与指令回复共用同一条投递链路与判据，避免自检通过而真实回复仍失败
        var outboxId = await new ChannelReplyService(_db, _gate).QueueAsync(route.Id, probe);
        if (outboxId is null) throw new BusinessException("自检消息未入队：原路回复窗口已过或该消息的回复条数已用尽，请重发一条消息后再试");
        var row = await _db.ChannelOutboxes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == outboxId);
        return new ChannelDeliveryDto
        {
            Id = row.Id, Platform = platform, Status = row.Status, Purpose = "Test",
            ErrorCode = row.LastErrorCode, Attempts = row.Attempts,
            CreatedAtUtc = row.CreatedAtUtc, AcceptedAtUtc = row.AcceptedAtUtc
        };
    }

    /// <summary>Unknown 可能已被平台受理；仅在 Manager 已核对手机/平台回执并明确确认时可人工重试。</summary>
    public async Task<ChannelDeliveryDto> RetryDeliveryAsync(string platform, string outboxId, bool confirm)
    {
        platform = RequirePlatform(platform);
        if (!confirm) throw new BusinessException("未知回执可能重复发送，须人工核对后明确确认");
        using var held = await _gate.AcquireAsync(platform);
        await using var tx = await _db.Database.BeginTransactionAsync();
        var account = await _db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Platform == platform && x.Enabled);
        var row = account is null ? null : await _db.ChannelOutboxes.FirstOrDefaultAsync(x => x.Id == outboxId && x.AccountId == account.Id);
        if (row is null || row.Status is not ("Failed" or "Unknown"))
            throw new BusinessException("投递记录不可重试，或通道未启用");
        var route = await _db.ChannelReplyRoutes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == row.ReplyRouteId);
        if (route is null || route.ExpiresAtUtc <= DateTime.UtcNow ||
            route.AccountId != account.Id || route.BindingVersion != account.BindingVersion ||
            row.BindingVersion != account.BindingVersion || row.PeerId != route.PeerId)
            throw new BusinessException("原路回复已过期或绑定已变化，禁止改用其他账号重试");
        row.Status = "Pending";
        row.NextAttemptAtUtc = DateTime.UtcNow;
        row.LastErrorCode = null;
        // 幂等凭据保持原 msg_seq/client_id，不生成新发送标识。
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return new ChannelDeliveryDto
        {
            Id = row.Id, Platform = platform, Status = row.Status, Purpose = row.Purpose,
            Attempts = row.Attempts, CreatedAtUtc = row.CreatedAtUtc, AcceptedAtUtc = row.AcceptedAtUtc
        };
    }

    /// <summary>保存 QQ 凭据，默认关闭；换账号必须显式确认，原子吊销旧绑定/路由/待发消息。</summary>
    public async Task<ChannelStatusDto> ConfigureQqAsync(QqChannelSaveDto request)
    {
        var appId = request?.AppId?.Trim();
        var appSecret = request?.AppSecret?.Trim();
        if (string.IsNullOrWhiteSpace(appId) || appId.Length > 128 ||
            string.IsNullOrWhiteSpace(appSecret) || appSecret.Length > 512)
            throw new BusinessException("QQ AppID / AppSecret 格式无效");
        // 平台对新增机器人默认启用 IP 白名单，正式环境只放行白名单公网 IP；沙箱环境不受该限制。
        // 沙箱地址留空即用官方默认接入点，不必要求用户去开放平台「沙箱配置」复制；显式地址仍过信任校验。
        var apiBase = ChannelNetwork.ResolveQqApiBase(request?.Environment, request?.ApiBase);
        if (!_secret.Available) throw new BusinessException($"通道主密钥不可用（{_secret.AvailabilityError ?? "未配置"}），恢复后才能保存凭据");
        using var held = await _gate.AcquireAsync(Qq);
        await using var tx = await _db.Database.BeginTransactionAsync();
        var account = await _db.ChannelAccounts.FirstOrDefaultAsync(x => x.Platform == Qq);
        if (account is null)
        {
            account = new ChannelAccountModel { Platform = Qq, BotId = appId, Enabled = false };
            _db.ChannelAccounts.Add(account);
        }
        else
        {
            await RevokeAsync(account);
            account.BotId = appId;
        }
        account.CredentialCiphertext = _secret.Protect(JsonSerializer.Serialize(
            new { AppId = appId, AppSecret = appSecret, ApiBase = apiBase }), account.Id, "qq-credentials");
        account.Enabled = true;
        account.LastErrorCode = null;
        account.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return (await StatusAsync()).First(x => x.Platform == Qq);
    }

    public async Task<QqChallengeDto> StartQqChallengeAsync(bool confirmRebind)
    {
        using var held = await _gate.AcquireAsync(Qq);
        var account = await _db.ChannelAccounts.FirstOrDefaultAsync(x => x.Platform == Qq && x.CredentialCiphertext != null);
        if (account is null) throw new BusinessException("请先配置 QQ 官方机器人");
        await using var tx = await _db.Database.BeginTransactionAsync();
        if (await _db.ChannelBindings.AnyAsync(x => x.AccountId == account.Id && x.Active))
        {
            if (!confirmRebind) throw new BusinessException("重新绑定会撤销旧用户与待发消息，需明确确认");
            await RevokeAsync(account);
        }
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        account.ChallengeHash = _secret.HashChallenge(account.Id, code);
        account.ChallengeExpiresAtUtc = DateTime.UtcNow.AddMinutes(5);
        account.ChallengeAttempts = 0;
        account.CandidatePeerId = null;
        account.CandidateAtUtc = null;
        account.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return new QqChallengeDto { Code = code, ExpiresAtUtc = account.ChallengeExpiresAtUtc.Value };
    }

    /// <summary>Gateway 接收的 C2C 私聊中仅对未绑定阶段的挑战码做恒时校验；返回是否已提名候选。</summary>
    public Task<bool> ObserveQqChallengeAsync(string accountId, string peerId, string content)
        => ObserveChallengeAsync(accountId, Qq, peerId, content);

    public Task<bool> ObserveWeixinChallengeAsync(string accountId, string peerId, string content)
        => ObserveChallengeAsync(accountId, Weixin, peerId, content);

    public Task<bool> ObserveFeishuChallengeAsync(string accountId, string peerId, string content)
        => ObserveChallengeAsync(accountId, Feishu, peerId, content);

    /// <summary>保存飞书应用凭据，默认关闭；换应用必须显式确认，原子吊销旧绑定/路由/待发消息。</summary>
    public async Task<ChannelStatusDto> ConfigureFeishuAsync(FeishuChannelSaveDto request)
    {
        var appId = request?.AppId?.Trim();
        var appSecret = request?.AppSecret?.Trim();
        if (string.IsNullOrWhiteSpace(appId) || appId.Length > 128 ||
            string.IsNullOrWhiteSpace(appSecret) || appSecret.Length > 512)
            throw new BusinessException("飞书 AppID / AppSecret 格式无效");
        if (!_secret.Available) throw new BusinessException($"通道主密钥不可用（{_secret.AvailabilityError ?? "未配置"}），恢复后才能保存凭据");
        using var held = await _gate.AcquireAsync(Feishu);
        await using var tx = await _db.Database.BeginTransactionAsync();
        var account = await _db.ChannelAccounts.FirstOrDefaultAsync(x => x.Platform == Feishu);
        if (account is null)
        {
            account = new ChannelAccountModel { Platform = Feishu, BotId = appId, Enabled = false };
            _db.ChannelAccounts.Add(account);
        }
        else
        {
            await RevokeAsync(account);
            account.BotId = appId;
        }
        // 长连接建连时鉴权，只需 AppID/AppSecret；不落 EncryptKey/VerificationToken（那属 Webhook 形态，本通道不用）
        account.CredentialCiphertext = _secret.Protect(JsonSerializer.Serialize(
            new { AppId = appId, AppSecret = appSecret }), account.Id, "feishu-credentials");
        account.Enabled = true;
        account.LastErrorCode = null;
        account.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return (await StatusAsync()).First(x => x.Platform == Feishu);
    }

    public async Task<FeishuChallengeDto> StartFeishuChallengeAsync(bool confirmRebind)
        => await StartChallengeAsync(Feishu, confirmRebind);

    public async Task<ChannelStatusDto> ConfirmFeishuBindingAsync(bool confirm)
        => await ConfirmBindingAsync(Feishu, confirm);

    /// <summary>
    /// 飞书扫码授权完成：凭据来自飞书设备码注册流程自动创建的个人应用，直接加密落库；
    /// 扫码人 open_id 即候选私聊对象，平台未返回 open_id 时退回一次性挑战码。
    /// Manager 二次确认前绝不开放业务消息，语义与微信扫码登记一致。
    /// </summary>
    public async Task<string> RegisterFeishuScanAsync(string appId, string appSecret, string openId)
    {
        using var held = await _gate.AcquireAsync(Feishu);
        if (string.IsNullOrWhiteSpace(appId) || appId.Length > 128 ||
            string.IsNullOrWhiteSpace(appSecret) || appSecret.Length > 512 ||
            openId is { Length: > 128 })
            throw new BusinessException("飞书扫码结果缺少必要字段或格式无效");
        await using var tx = await _db.Database.BeginTransactionAsync();
        var account = await _db.ChannelAccounts.FirstOrDefaultAsync(x => x.Platform == Feishu);
        if (account is null)
        {
            account = new ChannelAccountModel { Platform = Feishu };
            _db.ChannelAccounts.Add(account);
        }
        else await RevokeAsync(account);
        account.BotId = appId;
        account.CredentialCiphertext = _secret.Protect(JsonSerializer.Serialize(
            new { AppId = appId, AppSecret = appSecret }), account.Id, "feishu-credentials");
        account.CandidatePeerId = null;
        account.CandidateAtUtc = null;
        account.Enabled = true;
        account.LastErrorCode = null;
        account.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return null;
    }

    private async Task<FeishuChallengeDto> StartChallengeAsync(string platform, bool confirmRebind)
    {
        using var held = await _gate.AcquireAsync(platform);
        var account = await _db.ChannelAccounts.FirstOrDefaultAsync(x => x.Platform == platform && x.CredentialCiphertext != null);
        if (account is null) throw new BusinessException("请先配置该平台机器人");
        await using var tx = await _db.Database.BeginTransactionAsync();
        if (await _db.ChannelBindings.AnyAsync(x => x.AccountId == account.Id && x.Active))
        {
            if (!confirmRebind) throw new BusinessException("重新绑定会撤销旧用户与待发消息，需明确确认");
            await RevokeAsync(account);
        }
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        account.ChallengeHash = _secret.HashChallenge(account.Id, code);
        account.ChallengeExpiresAtUtc = DateTime.UtcNow.AddMinutes(5);
        account.ChallengeAttempts = 0;
        account.CandidatePeerId = null;
        account.CandidateAtUtc = null;
        account.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return new FeishuChallengeDto { Code = code, ExpiresAtUtc = account.ChallengeExpiresAtUtc.Value };
    }

    private async Task<ChannelStatusDto> ConfirmBindingAsync(string platform, bool confirm)
    {
        if (!confirm) throw new BusinessException("必须明确确认候选私聊账号");
        using var held = await _gate.AcquireAsync(platform);
        await using var tx = await _db.Database.BeginTransactionAsync();
        var account = await _db.ChannelAccounts.FirstOrDefaultAsync(x => x.Platform == platform);
        if (account?.CandidatePeerId is not { Length: > 0 } || account.CandidateAtUtc < DateTime.UtcNow.AddMinutes(-5))
            throw new BusinessException("没有有效的待确认私聊账号，请重新发起挑战");
        account.BindingVersion++;
        var binding = await _db.ChannelBindings.FirstOrDefaultAsync(x => x.Platform == platform);
        if (binding is null)
        {
            binding = new ChannelBindingModel { Platform = platform, AccountId = account.Id };
            _db.ChannelBindings.Add(binding);
        }
        binding.PeerId = account.CandidatePeerId;
        binding.Version = account.BindingVersion;
        binding.Active = true;
        binding.VerifiedAtUtc = DateTime.UtcNow;
        binding.RevokedAtUtc = null;
        account.CandidatePeerId = null;
        account.CandidateAtUtc = null;
        account.ChallengeHash = null;
        account.ChallengeExpiresAtUtc = null;
        account.Enabled = false; // 通过确认后还需管理员显式启用。
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return (await StatusAsync()).First(x => x.Platform == platform);
    }

    private async Task<bool> ObserveChallengeAsync(string accountId, string platform, string peerId, string content)
    {
        if (string.IsNullOrEmpty(peerId) || peerId.Length > 128) return false;
        var account = await _db.ChannelAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == accountId && x.Platform == platform);
        if (account?.ChallengeHash is not { Length: > 0 } hash || account.ChallengeExpiresAtUtc <= DateTime.UtcNow ||
            account.ChallengeAttempts >= 5 || account.CandidatePeerId != null) return false;
        var now = DateTime.UtcNow;
        var valid = content is { Length: 16 } && _secret.VerifyChallenge(accountId, content, hash);
        // 条件 UPDATE 是唯一的挑战码 Claim：新挑战码/已消费/过期/并发竞争的旧读值均影响 0 行。
        var available = _db.ChannelAccounts.Where(x => x.Id == accountId && x.Platform == platform &&
            x.ChallengeHash == hash && x.ChallengeExpiresAtUtc > now && x.ChallengeAttempts < 5 &&
            x.CandidatePeerId == null);
        int updated;
        if (valid)
            updated = await available.ExecuteUpdateAsync(update => update
                .SetProperty(x => x.CandidatePeerId, peerId)
                .SetProperty(x => x.CandidateAtUtc, now)
                .SetProperty(x => x.ChallengeHash, (string)null)
                .SetProperty(x => x.ChallengeAttempts, x => x.ChallengeAttempts + 1));
        else
            updated = await available.ExecuteUpdateAsync(update => update
                .SetProperty(x => x.ChallengeAttempts, x => x.ChallengeAttempts + 1)
                .SetProperty(x => x.ChallengeHash, x => x.ChallengeAttempts >= 4 ? null : x.ChallengeHash));
        // ExecuteUpdate 绕过 EF 跟踪器；同 scope 中若曾配置账号，须防旧实体在 SaveChanges 时覆写 Candidate。
        if (updated > 0)
            foreach (var entry in _db.ChangeTracker.Entries<ChannelAccountModel>().Where(x => x.Entity.Id == accountId).ToList())
                await entry.ReloadAsync();
        return valid && updated == 1;
    }

    public async Task<ChannelStatusDto> ConfirmQqBindingAsync(bool confirm)
    {
        if (!confirm) throw new BusinessException("必须明确确认候选私聊账号");
        using var held = await _gate.AcquireAsync(Qq);
        await using var tx = await _db.Database.BeginTransactionAsync();
        var account = await _db.ChannelAccounts.FirstOrDefaultAsync(x => x.Platform == Qq);
        if (account?.CandidatePeerId is not { Length: > 0 } || account.CandidateAtUtc < DateTime.UtcNow.AddMinutes(-5))
            throw new BusinessException("没有有效的待确认 QQ 私聊用户，请重新发起挑战");
        account.BindingVersion++;
        var binding = await _db.ChannelBindings.FirstOrDefaultAsync(x => x.Platform == Qq);
        if (binding is null)
        {
            binding = new ChannelBindingModel { Platform = Qq, AccountId = account.Id };
            _db.ChannelBindings.Add(binding);
        }
        binding.PeerId = account.CandidatePeerId;
        binding.Version = account.BindingVersion;
        binding.Active = true;
        binding.VerifiedAtUtc = DateTime.UtcNow;
        binding.RevokedAtUtc = null;
        account.CandidatePeerId = null;
        account.CandidateAtUtc = null;
        account.ChallengeHash = null;
        account.ChallengeExpiresAtUtc = null;
        account.Enabled = false; // 通过确认后还需管理员显式启用。
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return (await StatusAsync()).First(x => x.Platform == Qq);
    }

    public async Task<ChannelStatusDto> SetEnabledAsync(string platform, bool enabled)
    {
        platform = RequirePlatform(platform);
        using var held = await _gate.AcquireAsync(platform);
        var account = await _db.ChannelAccounts.FirstOrDefaultAsync(x => x.Platform == platform);
        if (account is null) throw new BusinessException("通道未配置");
        if (enabled && (!_secret.Available || string.IsNullOrEmpty(account.CredentialCiphertext)))
            throw new BusinessException("请先配置机器人凭据");
        account.Enabled = enabled;
        account.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (await StatusAsync()).First(x => x.Platform == platform);
    }

    public async Task<ChannelStatusDto> UnbindAsync(string platform, bool confirm)
    {
        platform = RequirePlatform(platform);
        if (!confirm) throw new BusinessException("解绑必须明确确认");
        using var held = await _gate.AcquireAsync(platform);
        await using var tx = await _db.Database.BeginTransactionAsync();
        var account = await _db.ChannelAccounts.FirstOrDefaultAsync(x => x.Platform == platform);
        if (account is null) throw new BusinessException("通道未配置");
        await RevokeAsync(account);
        account.CredentialCiphertext = null; // 断开时清除全部平台凭据；重新接入需重新配置/扫码。
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return (await StatusAsync()).First(x => x.Platform == platform);
    }

    /// <summary>外部扫码完成时只登记候选，Manager 二次确认前绝不开放业务消息。</summary>
    public async Task<string> RegisterWeixinScanAsync(string botId, string botToken, string peerId, string apiBase,
        string clientVersion = null, string channelVersion = null)
    {
        using var held = await _gate.AcquireAsync(Weixin);
        if (string.IsNullOrWhiteSpace(botId) || string.IsNullOrWhiteSpace(botToken) ||
            botId.Length > 128 || peerId is { Length: > 128 } || botToken.Length > 4096)
            throw new BusinessException("微信扫码结果缺少必要字段");
        apiBase = ChannelNetwork.WeixinUri("", apiBase).AbsoluteUri;
        if (clientVersion is not null || channelVersion is not null)
            WeixinQrLoginService.ValidateVersions(clientVersion, channelVersion);
        await using var tx = await _db.Database.BeginTransactionAsync();
        var account = await _db.ChannelAccounts.FirstOrDefaultAsync(x => x.Platform == Weixin);
        if (account is null)
        {
            account = new ChannelAccountModel { Platform = Weixin };
            _db.ChannelAccounts.Add(account);
        }
        else await RevokeAsync(account);
        account.BotId = botId;
        account.CredentialCiphertext = _secret.Protect(JsonSerializer.Serialize(new
        {
            BotToken = botToken, ApiBase = apiBase,
            ClientVersion = clientVersion, ChannelVersion = channelVersion
        }), account.Id, "weixin-credentials");
        account.CandidatePeerId = null;
        account.CandidateAtUtc = null;
        account.Enabled = true;
        account.LastErrorCode = null;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return null;
    }

    public async Task<ChannelStatusDto> ConfirmWeixinBindingAsync(bool confirm)
    {
        if (!confirm) throw new BusinessException("必须明确确认扫码账号");
        using var held = await _gate.AcquireAsync(Weixin);
        await using var tx = await _db.Database.BeginTransactionAsync();
        var account = await _db.ChannelAccounts.FirstOrDefaultAsync(x => x.Platform == Weixin);
        if (account?.CandidatePeerId is not { Length: > 0 } || account.CandidateAtUtc < DateTime.UtcNow.AddMinutes(-5))
            throw new BusinessException("微信扫码候选已过期，请重新扫码");
        account.BindingVersion++;
        var binding = await _db.ChannelBindings.FirstOrDefaultAsync(x => x.Platform == Weixin);
        if (binding is null)
        {
            binding = new ChannelBindingModel { Platform = Weixin, AccountId = account.Id };
            _db.ChannelBindings.Add(binding);
        }
        binding.PeerId = account.CandidatePeerId;
        binding.Version = account.BindingVersion;
        binding.Active = true;
        binding.RevokedAtUtc = null;
        binding.VerifiedAtUtc = DateTime.UtcNow;
        account.CandidatePeerId = null;
        account.CandidateAtUtc = null;
        account.ChallengeHash = null;
        account.ChallengeExpiresAtUtc = null;
        account.Enabled = false;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return (await StatusAsync()).First(x => x.Platform == Weixin);
    }

    private async Task RevokeAsync(ChannelAccountModel account)
    {
        account.Enabled = false;
        account.BindingVersion++;
        account.ChallengeHash = null;
        account.ChallengeExpiresAtUtc = null;
        account.CandidatePeerId = null;
        account.CandidateAtUtc = null;
        account.UpdatedAtUtc = DateTime.UtcNow;
        var binding = await _db.ChannelBindings.FirstOrDefaultAsync(x => x.Platform == account.Platform);
        if (binding is not null)
        {
            binding.Active = false;
            binding.RevokedAtUtc = DateTime.UtcNow;
        }
        await _db.ChannelOutboxes.Where(x => x.AccountId == account.Id && (x.Status == "Pending" || x.Status == "Retry" || x.Status == "Sending"))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Cancelled"));
        await _db.ChannelReplyRoutes.Where(x => x.AccountId == account.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ContextTokenCiphertext, (string)null)
                                     .SetProperty(x => x.ExpiresAtUtc, DateTime.UtcNow));
        await _db.ChannelCursors.Where(x => x.AccountId == account.Id).ExecuteDeleteAsync();
        await _db.ChannelAllowedCommands.Where(x => x.AccountId == account.Id).ExecuteDeleteAsync();
    }

    private static string Mask(string value) => string.IsNullOrEmpty(value) ? null :
        value.Length <= 8 ? "****" : value[..4] + "****" + value[^4..];

    /// <summary>QQ 接入点环境提示（只回标识，不回完整地址）；缺主密钥或凭据异常时静默留空，不影响状态页可用。</summary>
    private string QqEnvironment(ChannelAccountModel account)
    {
        if (account?.CredentialCiphertext is not { Length: > 0 } || !_secret.Available) return null;
        try
        {
            using var parsed = JsonDocument.Parse(_secret.Unprotect(account.CredentialCiphertext, account.Id, "qq-credentials"));
            var base64 = QqChannelClient.Text(parsed.RootElement, "ApiBase") ?? ChannelNetwork.QqProdApiBase;
            return base64.Contains("sandbox", StringComparison.OrdinalIgnoreCase) ? "Sandbox" : "Production";
        }
        catch { return null; }
    }

    private static string Fingerprint(string value) => string.IsNullOrEmpty(value) ? null :
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];
}
