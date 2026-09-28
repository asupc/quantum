using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quantum.Entities.DTOs;
using Quantum.Utils;

namespace Quantum.Application.Channels;

/// <summary>Manager 扫码状态机；二维码/验证码不写日志，确认令牌立即加密入库，只回脱敏状态。</summary>
public sealed class WeixinQrLoginService
{
    private readonly ChannelNetwork _network;
    private readonly IServiceScopeFactory _scopes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PendingLogin _pending;

    private sealed record PendingLogin(string SessionId, string QrId, string ApiBase, DateTime ExpiresAtUtc,
        string ClientVersion, string ChannelVersion);

    public WeixinQrLoginService(ChannelNetwork network, IServiceScopeFactory scopes)
    {
        _network = network;
        _scopes = scopes;
    }

    /// <summary>
    /// iLink 协议版本字段解析：入参 → 环境变量 → 内置默认值，**留空不再拒绝**。
    /// 这两个值不是密钥、也不是身份凭据：channel_version 就是腾讯官方渠道插件的版本号（公开信息），
    /// iLink-App-ClientVersion 在官方样例中固定为 "1"。早期实现把它们当成「必须人工核对的机密」，
    /// 缺失即抛错，等于把扫码流程第一步自己锁死——现已改为「默认可用、需要时可覆盖」。
    /// 仍然校验显式传入值的格式，避免把脏值发给平台。
    /// </summary>
    public const string DefaultClientVersion = "1";
    public const string DefaultChannelVersion = "1.0.3";

    public static (string ClientVersion, string ChannelVersion) ResolveVersions(string client, string channel)
    {
        client = FirstNonBlank(client, Environment.GetEnvironmentVariable("QUANTUM_CHANNEL_WEIXIN_CLIENT_VERSION"),
            DefaultClientVersion);
        channel = FirstNonBlank(channel, Environment.GetEnvironmentVariable("QUANTUM_CHANNEL_WEIXIN_CHANNEL_VERSION"),
            DefaultChannelVersion);
        if (!uint.TryParse(client, out var parsed) || parsed == 0)
            throw new BusinessException("iLink 客户端版本须为十进制正整数");
        if (string.IsNullOrWhiteSpace(channel) || channel.Length > 32 ||
            channel.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')))
            throw new BusinessException("channel_version 仅限字母、数字与 . - _，且不超过 32 字符");
        return (client, channel);
    }

    /// <summary>旧名保留给既有调用方与测试，语义已变为「解析并兜底默认值」。</summary>
    public static (string ClientVersion, string ChannelVersion) ValidateVersions(string client, string channel)
        => ResolveVersions(client, channel);

    private static string FirstNonBlank(params string[] candidates)
        => candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    public async Task<WeixinQrStartDto> StartAsync(WeixinQrStartRequestDto request, CancellationToken ct)
    {
        var (version, channelVersion) = ValidateVersions(request?.ClientVersion, request?.ChannelVersion);
        var confirmRebind = request.ConfirmRebind;
        await _gate.WaitAsync(ct);
        try
        {
            _pending = null;
            using var scope = _scopes.CreateScope();
            var admin = scope.ServiceProvider.GetRequiredService<ChannelManagementService>();
            var old = (await admin.StatusAsync()).First(x => x.Platform == ChannelManagementService.Weixin);
            var protector = scope.ServiceProvider.GetRequiredService<ChannelSecretProtector>();
            if (!protector.Available)
                throw new BusinessException($"通道主密钥不可用（{protector.AvailabilityError ?? "未配置"}），恢复后可重新扫码");
            if (old.Configured) await admin.UnbindAsync(ChannelManagementService.Weixin, true);
            // 官方协议：取二维码是 **GET 无请求体**（早期实现误用 POST，导致扫码第一步就拿不到二维码）。
            var result = await _network.GetAsync(ChannelNetwork.WeixinUri("ilink/bot/get_bot_qrcode?bot_type=3"),
                version: version, ct: ct);
            var qr = GetText(result, "qrcode");
            var content = GetText(result, "qrcode_img_content");
            if (string.IsNullOrWhiteSpace(qr) || string.IsNullOrWhiteSpace(content) || qr.Length > 4096 || content.Length > 16384)
                throw new ChannelProtocolException("QR_INVALID");
            _pending = new PendingLogin(Guid.NewGuid().ToString("N"), qr, "https://ilinkai.weixin.qq.com/",
                DateTime.UtcNow.AddMinutes(5), version, channelVersion);
            return new WeixinQrStartDto { SessionId = _pending.SessionId, QrContent = content, ExpiresAtUtc = _pending.ExpiresAtUtc };
        }
        finally { _gate.Release(); }
    }

    public async Task<WeixinQrStatusDto> PollAsync(string sessionId, string verifyCode, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = _pending;
            if (state is null || state.SessionId != sessionId || state.ExpiresAtUtc <= DateTime.UtcNow)
                throw new BusinessException("二维码会话不存在或已过期，请重新发起扫码");
            if (!string.IsNullOrEmpty(verifyCode) && (verifyCode.Length > 32 || verifyCode.Any(char.IsControl)))
                throw new BusinessException("验证码格式无效");
            var path = "ilink/bot/get_qrcode_status?qrcode=" + Uri.EscapeDataString(state.QrId);
            if (!string.IsNullOrEmpty(verifyCode)) path += "&verify_code=" + Uri.EscapeDataString(verifyCode);
            var result = await _network.GetAsync(ChannelNetwork.WeixinUri(path, state.ApiBase), version: state.ClientVersion, ct: ct);
            var ret = QqChannelClient.Int(result, "ret");
            var err = QqChannelClient.Int(result, "errcode");
            if (ret is not null and not 0 || err is not null and not 0)
                throw new ChannelProtocolException("QR_RET_" + (ret?.ToString() ?? err?.ToString()));
            var status = GetText(result, "status") ?? "unknown";
            switch (status)
            {
                case "wait" or "scaned" or "need_verifycode": return new WeixinQrStatusDto { Status = status };
                case "scaned_but_redirect":
                    var redirect = GetText(result, "redirect_host");
                    if (string.IsNullOrEmpty(redirect)) throw new ChannelProtocolException("QR_REDIRECT_MISSING");
                    var normalized = redirect.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? redirect : "https://" + redirect;
                    var safe = ChannelNetwork.WeixinUri("", normalized);
                    _pending = state with { ApiBase = safe.AbsoluteUri };
                    return new WeixinQrStatusDto { Status = "scaned" };
                case "confirmed":
                    var botToken = GetText(result, "bot_token");
                    var botId = GetText(result, "ilink_bot_id");
                    var peerId = GetText(result, "ilink_user_id");
                    if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(botId))
                        throw new BusinessException("扫码结果缺少机器人令牌或 ID");
                    var baseUrl = GetText(result, "baseurl") ?? state.ApiBase;
                    baseUrl = ChannelNetwork.WeixinUri("", baseUrl).AbsoluteUri;
                    using (var scope = _scopes.CreateScope())
                    {
                        var admin = scope.ServiceProvider.GetRequiredService<ChannelManagementService>();
                        var challenge = await admin.RegisterWeixinScanAsync(botId, botToken, peerId, baseUrl,
                            state.ClientVersion, state.ChannelVersion);
                        var current = (await admin.StatusAsync()).First(x => x.Platform == ChannelManagementService.Weixin);
                        _pending = null;
                        return new WeixinQrStatusDto { Status = "confirmed" };
                    }
                case "expired" or "verify_code_blocked" or "binded_redirect":
                    _pending = null;
                    return new WeixinQrStatusDto { Status = status };
                default: throw new ChannelProtocolException("QR_UNKNOWN_STATUS");
            }
        }
        finally { _gate.Release(); }
    }

    private static string GetText(JsonElement obj, string field)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
