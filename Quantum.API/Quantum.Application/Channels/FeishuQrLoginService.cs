using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quantum.Entities.DTOs;
using Quantum.Utils;

namespace Quantum.Application.Channels;

/// <summary>
/// 飞书扫码绑定状态机（设备码授权注册流程，参照 TerminalBuddy 的 bot_scan 实现）：
/// accounts.feishu.cn/oauth/v1/app/registration 的 init / begin / poll——begin 返回
/// verification_uri_complete 渲染成二维码，用户用飞书 App 扫码确认后，飞书在扫码人账号下
/// 自动创建个人应用机器人，poll 即可拿到 AppID/AppSecret 与扫码人 open_id，免去手动到
/// 开发者后台创建应用。device_code 只存内存随会话失效；AppSecret 立即加密入库，
/// 不回传前端、不写日志；绑定仍走「候选 + Manager 二次确认」。
/// </summary>
public sealed class FeishuQrLoginService
{
    internal static readonly Uri RegistrationUri =
        ChannelNetwork.FeishuUri("/oauth/v1/app/registration", "https://accounts.feishu.cn/");

    private readonly ChannelNetwork _network;
    private readonly IServiceScopeFactory _scopes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PendingScan _pending;

    private sealed record PendingScan(string SessionId, string DeviceCode, DateTime ExpiresAtUtc, int IntervalSeconds);

    public FeishuQrLoginService(ChannelNetwork network, IServiceScopeFactory scopes)
    {
        _network = network;
        _scopes = scopes;
    }

    public async Task<FeishuQrStartDto> StartAsync(bool confirmRebind, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _pending = null;
            using var scope = _scopes.CreateScope();
            var admin = scope.ServiceProvider.GetRequiredService<ChannelManagementService>();
            var protector = scope.ServiceProvider.GetRequiredService<ChannelSecretProtector>();
            if (!protector.Available)
                throw new BusinessException($"通道主密钥不可用（{protector.AvailabilityError ?? "未配置"}），恢复后可重新扫码");
            var old = (await admin.StatusAsync()).First(x => x.Platform == ChannelManagementService.Feishu);
            if (old.Configured) await admin.UnbindAsync(ChannelManagementService.Feishu, true);
            // 1. init：确认飞书侧支持 client_secret 认证形态。
            var init = await _network.PostFormAsync(RegistrationUri, [new KeyValuePair<string, string>("action", "init")],
                ct, readErrorBody: true);
            if (!SupportsClientSecret(init)) throw new ChannelProtocolException("FEISHU_SCAN_AUTH_METHOD");
            // 2. begin：拿 device_code 与二维码地址（PersonalAgent = 扫码人在自己账号下创建个人应用机器人）。
            var begin = await _network.PostFormAsync(RegistrationUri,
            [
                new KeyValuePair<string, string>("action", "begin"),
                new KeyValuePair<string, string>("archetype", "PersonalAgent"),
                new KeyValuePair<string, string>("auth_method", "client_secret"),
                new KeyValuePair<string, string>("request_user_info", "open_id")
            ], ct, readErrorBody: true);
            var (deviceCode, qrUrl, interval, expireIn) = ParseBegin(begin);
            _pending = new PendingScan(Guid.NewGuid().ToString("N"), deviceCode,
                DateTime.UtcNow.AddSeconds(expireIn), interval);
            return new FeishuQrStartDto
            {
                SessionId = _pending.SessionId, QrUrl = qrUrl,
                IntervalSeconds = interval, ExpiresAtUtc = _pending.ExpiresAtUtc
            };
        }
        finally { _gate.Release(); }
    }

    public async Task<FeishuQrStatusDto> PollAsync(string sessionId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = _pending;
            if (state is null || state.SessionId != sessionId || state.ExpiresAtUtc <= DateTime.UtcNow)
                throw new BusinessException("二维码会话不存在或已过期，请重新发起扫码");
            var result = await _network.PostFormAsync(RegistrationUri,
            [
                new KeyValuePair<string, string>("action", "poll"),
                new KeyValuePair<string, string>("device_code", state.DeviceCode),
                new KeyValuePair<string, string>("tp", "ob_app")
            ], ct, readErrorBody: true);
            var outcome = ParsePoll(result);
            switch (outcome.Status)
            {
                case "waiting":
                    return new FeishuQrStatusDto { Status = "waiting" };
                case "expired":
                    _pending = null;
                    return new FeishuQrStatusDto { Status = "expired" };
                case "failed":
                    _pending = null;
                    return new FeishuQrStatusDto { Status = "failed", Error = outcome.Error ?? "飞书扫码授权失败" };
            }
            // success：凭据立即经 ChannelManagementService 加密入库；open_id 缺失则退回一次性挑战码。
            if (string.IsNullOrWhiteSpace(outcome.AppId) || string.IsNullOrWhiteSpace(outcome.AppSecret))
                throw new ChannelProtocolException("FEISHU_SCAN_CREDENTIALS_INCOMPLETE");
            using var confirmScope = _scopes.CreateScope();
            var management = confirmScope.ServiceProvider.GetRequiredService<ChannelManagementService>();
            var challenge = await management.RegisterFeishuScanAsync(outcome.AppId, outcome.AppSecret, outcome.OpenId);
            var current = (await management.StatusAsync()).First(x => x.Platform == ChannelManagementService.Feishu);
            _pending = null;
            return new FeishuQrStatusDto
            {
                Status = "confirmed"
            };
        }
        finally { _gate.Release(); }
    }

    internal static bool SupportsClientSecret(JsonElement init)
        => init.ValueKind == JsonValueKind.Object &&
           init.TryGetProperty("supported_auth_methods", out var methods) &&
           methods.ValueKind == JsonValueKind.Array &&
           methods.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "client_secret");

    internal static (string DeviceCode, string QrUrl, int Interval, int ExpireIn) ParseBegin(JsonElement begin)
    {
        var deviceCode = FeishuChannelClient.Text(begin, "device_code");
        var qrUrl = FeishuChannelClient.Text(begin, "verification_uri_complete");
        if (string.IsNullOrWhiteSpace(deviceCode) || deviceCode.Length > 1024 ||
            string.IsNullOrWhiteSpace(qrUrl) || qrUrl.Length > 2048)
            throw new ChannelProtocolException("FEISHU_SCAN_BEGIN_INVALID");
        var interval = FeishuChannelClient.Int(begin, "interval") ?? 5;
        var expireIn = FeishuChannelClient.Int(begin, "expire_in") ?? 600;
        return (deviceCode, qrUrl, Math.Clamp(interval, 2, 60), Math.Clamp(expireIn, 60, 1800));
    }

    internal sealed record FeishuScanPollOutcome(string Status, string AppId, string AppSecret, string OpenId, string Error);

    /// <summary>设备码 poll：等待态由飞书以 4xx + JSON(error) 返回（readErrorBody 已统一进 body 解析），
    /// 故 body 无凭证且无已识别 error 时一律按 waiting 处理，不猜协议外状态。</summary>
    internal static FeishuScanPollOutcome ParsePoll(JsonElement result)
    {
        var appId = FeishuChannelClient.Text(result, "client_id");
        var appSecret = FeishuChannelClient.Text(result, "client_secret");
        if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(appSecret))
        {
            var openId = result.ValueKind == JsonValueKind.Object &&
                         result.TryGetProperty("user_info", out var info) && info.ValueKind == JsonValueKind.Object
                ? FeishuChannelClient.Text(info, "open_id") : null;
            return new FeishuScanPollOutcome("success", appId, appSecret, openId, null);
        }
        return FeishuChannelClient.Text(result, "error") switch
        {
            "access_denied" => new FeishuScanPollOutcome("failed", null, null, null, "用户拒绝授权"),
            "expired_token" => new FeishuScanPollOutcome("expired", null, null, null, null),
            _ => new FeishuScanPollOutcome("waiting", null, null, null, null)
        };
    }
}
