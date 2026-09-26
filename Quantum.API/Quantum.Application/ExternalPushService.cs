using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Application;

/// <summary>
/// 外部受限富文本推送（G-Push）：第三方经专属 PushKey 凭据向管理员设备推送一条通知，
/// 并可为该消息指定会话标题；同接入方同标题归同一会话，不同接入方同名标题互不相干。
///
/// 三条不可让的边界：
/// 1. **只发通知**——绝不入指令队列、绝不触发任务/脚本/AI 工具、绝不携带管理权限；
/// 2. **成功只等于已持久化**——提交后的 WS 投递与送达标记是 best-effort（审核项 R-06）；
/// 3. **幂等行未物理清理前，同键恒按原请求去重**（审核项 R-07）。
/// </summary>
public class ExternalPushService
{
    /// <summary>正文长度上限（字符）</summary>
    public const int MaxContentLength = 4096;

    /// <summary>单条正文允许的有效富文本标记上限</summary>
    public const int MaxMarkers = 50;

    /// <summary>请求体字节上限（UTF-8）；边缘层（Kestrel/反代）先行拒绝的 413 不在本保证范围内</summary>
    public const int MaxRequestBytes = 8 * 1024;

    public const int MaxTitleLength = 100;
    public const int MaxSessionTitleLength = 80;

    /// <summary>会话键命名空间前缀：固定留给外部会话，任务侧不得占用</summary>
    public const string SessionPrefix = "external:";

    /// <summary>幂等记录设计保留时长（到期后由清理任务物理删除，删除前同键仍按原请求去重）</summary>
    internal static TimeSpan IdempotencyRetention = TimeSpan.FromHours(72);

    private static readonly string[] AllowedColors = ["red", "green", "orange", "blue", "purple", "gray"];

    /// <summary>完整标记形态：{{颜色|文字}} / {{tag:颜色|文字}} / {{link:文字|URL}}</summary>
    private static readonly Regex MarkerPattern =
        new(@"\{\{(?<body>[^{}]*?)\}\}", RegexOptions.Compiled);

    private static readonly Regex ColorBodyPattern =
        new(@"^(?<color>red|green|orange|blue|purple|gray)\|(?<text>[^|{}]+)$", RegexOptions.IgnoreCase);

    private static readonly Regex TagBodyPattern =
        new(@"^tag:(?<color>red|green|orange|blue|purple|gray)\|(?<text>[^|{}]+)$", RegexOptions.IgnoreCase);

    private static readonly Regex LinkBodyPattern =
        new(@"^link:(?<text>[^|{}]+)\|(?<url>[^|{}]+)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// 明文 URL（协议 + // 或 www. 前缀）。仅对**标记之外的普通文本**判定：Web/App 会把纯文本 URL
    /// 自动转成可点链接，只校验命名链接挡不住 http:// 绕过（审核项 R-10）——普通文本里一律拒绝，
    /// 要求改用 {{link:..|..}}。要求带 // 是为了不把 {{tag:orange|..}} 的冒号误判成协议。
    /// </summary>
    private static readonly Regex BareUrlPattern =
        new(@"(\b[a-zA-Z][a-zA-Z0-9+.\-]{1,15}:(//|\\)|\bwww\.)\S", RegexOptions.Compiled);

    /// <summary>危险协议：无论是否带 //，普通文本里一律拒绝。</summary>
    private static readonly Regex DangerousSchemePattern =
        new(@"\b(javascript|vbscript|data|intent|file|about):", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IQuantumDbContext _db;
    private readonly AppPushService _push;
    private readonly ILogger<ExternalPushService> _log;

    public ExternalPushService(IQuantumDbContext db, AppPushService push, ILogger<ExternalPushService> log)
    {
        _db = db;
        _push = push;
        _log = log;
    }

    /// <summary>推送结果：New=首次受理并提交；Duplicate=同键同内容命中旧请求（不重复广播）。</summary>
    public record PushOutcome(bool Duplicate, string NotificationId, string MsgId, string SessionKey);

    /// <summary>
    /// 受理一条外部推送。校验（含富文本与标题）→ 配额 → 同事务写幂等行+通知+会话镜像+标题 → 提交后才广播。
    /// </summary>
    public async Task<PushOutcome> SendAsync(ExternalPushCredentialModel credential, string title,
        string sessionTitle, string content, string idempotencyKey, int requestBytes)
    {
        if (credential == null || !credential.Enabled)
        {
            throw new BusinessException("推送凭据无效");
        }

        var normalizedTitle = Require(title, MaxTitleLength, "Title", allowEmpty: false, singleLine: true);
        var normalizedSession = string.IsNullOrWhiteSpace(sessionTitle)
            ? credential.DisplayName?.Trim()
            : Require(sessionTitle, MaxSessionTitleLength, "SessionTitle", allowEmpty: false, singleLine: true);
        var normalizedContent = RequireContent(content);
        var normalizedKey = RequireIdempotencyKey(idempotencyKey);

        if (requestBytes > MaxRequestBytes)
        {
            throw new BusinessException($"请求体超过 {MaxRequestBytes} 字节上限");
        }

        var hash = HashOf(normalizedTitle, normalizedSession, normalizedContent);
        var sessionKey = BuildSessionKey(credential.Id, normalizedSession);

        // 同键重放先短路：同内容回原结果（不重复广播），异内容拒绝——不依赖事务，省一次写入
        var existing = await _db.ExternalPushRequests.AsNoTracking()
            .FirstOrDefaultAsync(n => n.CredentialId == credential.Id && n.IdempotencyKey == normalizedKey);
        if (existing != null)
        {
            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
            {
                throw new BusinessException("Idempotency-Key 已用于不同内容的请求");
            }

            return new PushOutcome(true, existing.NotificationId, existing.MsgId, existing.SessionKey);
        }

        await EnforceQuotaAsync(credential);

        var msgId = Guid.NewGuid().ToString();
        // 通知标题只用请求的 Title：会话身份由 SessionTitle/DisplayTitle 单独承载（契约 §6.4「不得互换」）
        var result = await _push.SendNotificationWithSideEffectAsync(
            normalizedTitle, normalizedContent, "system", jump: null,
            sessionKey: sessionKey, sessionTitle: normalizedSession, msgIdOverride: msgId,
            preCommit: async () =>
            {
                _db.ExternalPushRequests.Add(new ExternalPushRequestModel
                {
                    CredentialId = credential.Id,
                    IdempotencyKey = normalizedKey,
                    RequestHash = hash,
                    NotificationId = null,
                    MsgId = msgId,
                    SessionKey = sessionKey,
                    PayloadBytes = requestBytes
                });
                await Task.CompletedTask;
            });

        if (result == null)
        {
            throw new BusinessException("推送失败，请稍后重试");
        }

        // 幂等行补写通知 Id（同事务内已提交主记录，这一步只为回查更快；失败不影响已提交结果）
        try
        {
            await _db.ExternalPushRequests
                .Where(n => n.CredentialId == credential.Id && n.IdempotencyKey == normalizedKey)
                .ExecuteUpdateAsync(n => n.SetProperty(p => p.NotificationId, result.Id)
                    .SetProperty(p => p.MsgId, result.MsgId));
            await TouchCredentialAsync(credential);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "外部推送幂等行回填失败（不影响已提交的通知）");
        }

        return new PushOutcome(false, result.Id, result.MsgId, sessionKey);
    }

    private async Task TouchCredentialAsync(ExternalPushCredentialModel credential)
    {
        try
        {
            await _db.ExternalPushCredentials
                .Where(n => n.Id == credential.Id)
                .ExecuteUpdateAsync(n => n.SetProperty(p => p.LastUsedAtUtc, DateTime.UtcNow)
                    .SetProperty(p => p.TotalSent, p => p.TotalSent + 1));
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "外部推送凭据使用计数失败");
        }
    }

    // ------------------------------------------------------------------ 配额与限流

    /// <summary>
    /// 按凭据的每分钟/每日配额计数（数据源就是幂等表，天然跨重启，不依赖进程内字典）。
    /// 计数失败即不放行（宁拒绝不失控）。
    /// </summary>
    private async Task EnforceQuotaAsync(ExternalPushCredentialModel credential)
    {
        var minuteAgo = DateTime.UtcNow.AddMinutes(-1);
        var dayAgo = DateTime.UtcNow.AddDays(-1);
        var inMinute = await _db.ExternalPushRequests.CountAsync(n =>
            n.CredentialId == credential.Id && n.CreatedAtUtc >= minuteAgo);
        if (credential.RateLimitPerMinute > 0 && inMinute >= credential.RateLimitPerMinute)
        {
            throw new BusinessException("超出每分钟推送配额，请稍后重试");
        }

        if (credential.DailyQuota > 0)
        {
            var inDay = await _db.ExternalPushRequests.CountAsync(n =>
                n.CredentialId == credential.Id && n.CreatedAtUtc >= dayAgo);
            if (inDay >= credential.DailyQuota)
            {
                throw new BusinessException("超出当日推送配额");
            }
        }
    }

    /// <summary>清理过期幂等记录（保留窗口之后才允许键复用）。批量限速，不碰通知与会话历史。</summary>
    public async Task<int> PruneIdempotencyRecordsAsync()
    {
        var cutoff = DateTime.UtcNow.Subtract(IdempotencyRetention);
        var victims = await _db.ExternalPushRequests.Where(n => n.CreatedAtUtc < cutoff)
            .OrderBy(n => n.CreatedAtUtc).Take(500).ToListAsync();
        if (victims.Count == 0)
        {
            return 0;
        }

        _db.ExternalPushRequests.RemoveRange(victims);
        await _db.SaveChangesAsync();
        LogServiceHelper.Info("外部推送幂等记录清理", $"删除 {victims.Count} 条（早于 {cutoff:yyyy-MM-dd HH:mm} UTC）", "ExternalPush");
        return victims.Count;
    }

    // ------------------------------------------------------------------ 校验

    private static string Require(string value, int max, string field, bool allowEmpty, bool singleLine)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            if (allowEmpty)
            {
                return null;
            }

            throw new BusinessException($"{field} 不能为空");
        }

        if (trimmed.Length > max)
        {
            throw new BusinessException($"{field} 超过 {max} 字符上限");
        }

        if (singleLine && trimmed.Any(char.IsControl))
        {
            throw new BusinessException($"{field} 不能包含换行或控制字符");
        }

        return trimmed;
    }

    /// <summary>
    /// 正文校验：长度 → 尖括号一律拒绝 → 逐个标记校验（未知/残缺即拒绝）→ 标记数上限
    /// → 标记之外的普通文本不得含明文 URL 或危险协议。
    /// 通过后的正文按项目现有富文本语法原样入库，两端自行渲染；绝不落 HTML。
    /// </summary>
    private static string RequireContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new BusinessException("Content 不能为空");
        }

        if (content.Length > MaxContentLength)
        {
            throw new BusinessException($"Content 超过 {MaxContentLength} 字符上限");
        }

        if (content.IndexOf('\uFFFD') >= 0)
        {
            throw new BusinessException("Content 含非法字符");
        }

        // 契约上「<script> 等字面内容应作为文字安全显示」，但那要求两端渲染器都先过转义测试；
        // 在双端转义用例落地前，外部输入里的尖括号一律拒绝，不开这条 HTML 可达面。
        if (content.IndexOf('<') >= 0 || content.IndexOf('>') >= 0)
        {
            throw new BusinessException("Content 不支持尖括号字符");
        }

        // 大括号不成对=残缺标记：按字面显示会让两端渲染分叉，直接拒绝
        var open = content.Count(c => c == '{');
        var close = content.Count(c => c == '}');
        if (open != close || open % 2 != 0)
        {
            throw new BusinessException("Content 含不完整的富文本标记");
        }

        var markers = MarkerPattern.Matches(content);
        if (open / 2 != markers.Count)
        {
            throw new BusinessException("Content 含无法识别的富文本标记");
        }

        if (markers.Count > MaxMarkers)
        {
            throw new BusinessException($"富文本标记超过 {MaxMarkers} 个上限");
        }

        foreach (Match marker in markers)
        {
            ValidateMarker(marker.Groups["body"].Value);
        }

        // 标记整体摘掉之后再查明文 URL：{{tag:orange|..}} 的冒号不是协议，链接地址也已在标记里校验过
        var plainText = MarkerPattern.Replace(content, " ");
        if (BareUrlPattern.IsMatch(plainText) || DangerousSchemePattern.IsMatch(plainText))
        {
            throw new BusinessException("Content 不支持明文链接，请改用 {{link:名称|https://...}} 命名链接");
        }

        return content;
    }

    private static void ValidateMarker(string body)
    {
        if (ColorBodyPattern.IsMatch(body) || TagBodyPattern.IsMatch(body))
        {
            return;
        }

        var link = LinkBodyPattern.Match(body);
        if (link.Success)
        {
            ValidateLinkUrl(link.Groups["url"].Value);
            return;
        }

        throw new BusinessException($"不支持的富文本标记：{{{{{body[..Math.Min(body.Length, 32)]}...}}}}");
    }

    /// <summary>命名链接只接受绝对 HTTPS：无 userinfo、无控制字符、协议白名单外一律拒绝。</summary>
    private static void ValidateLinkUrl(string rawUrl)
    {
        var url = rawUrl.Trim();
        if (url.Length == 0 || url.Any(char.IsControl) || url.Any(char.IsWhiteSpace))
        {
            throw new BusinessException("链接地址非法");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw new BusinessException("链接仅支持绝对 https 地址");
        }
    }

    /// <summary>Idempotency-Key：8~128 个 ASCII 可打印字符且不含空白。</summary>
    private static string RequireIdempotencyKey(string key)
    {
        var value = (key ?? string.Empty).Trim();
        if (value.Length is < 8 or > 128 || value.Any(c => c <= 0x20 || c >= 0x7F))
        {
            throw new BusinessException("Idempotency-Key 需为 8~128 个 ASCII 可打印字符且不含空白");
        }

        return value;
    }

    private static string HashOf(string title, string sessionTitle, string content)
    {
        var canonical = string.Join('\n',
            NormalizeTitle(title), NormalizeTitle(sessionTitle), content.Normalize(NormalizationForm.FormKC));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// 标题归一化：trim + Unicode NFKC（全角/兼容字符折叠，避免「看起来一样但分组不同」）+ 大小写敏感。
    /// 归一化后的精确值即分组键——同接入方改标题 = 新会话，不做隐式改名。
    /// </summary>
    public static string NormalizeTitle(string title)
        => string.IsNullOrWhiteSpace(title) ? string.Empty : title.Trim().Normalize(NormalizationForm.FormKC);

    /// <summary>
    /// 外部会话键：<c>external:{凭据Id}:{SHA256(归一化标题)}</c>——不含明文标题，
    /// 不同接入方同名标题天然不同键。
    /// </summary>
    public static string BuildSessionKey(string credentialId, string sessionTitle)
        => $"{SessionPrefix}{credentialId}:{Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(NormalizeTitle(sessionTitle)))).ToLowerInvariant()}";

    /// <summary>
    /// 上线前置检查：现存任务 Id / 任务会话名不得占用 external: 前缀（否则外部会话会与任务会话串台）。
    /// </summary>
    public async Task<List<string>> FindSessionPrefixConflictsAsync()
    {
        var tasks = await _db.Tasks.AsNoTracking()
            .Where(n => n.Id.StartsWith(SessionPrefix)
                        || (n.SessionName != null && n.SessionName.StartsWith(SessionPrefix)))
            .Select(n => n.Id)
            .ToListAsync();
        var sessions = await _db.ChatSessions.AsNoTracking()
            .Where(n => n.SessionKey.StartsWith(SessionPrefix))
            .Select(n => n.SessionKey)
            .ToListAsync();
        return tasks.Concat(sessions).Distinct().ToList();
    }

    /// <summary>请求体 UTF-8 字节数（用于 8 KiB 闸，按字节而非字符计）。</summary>
    public static int ByteLength(object body)
        => body == null ? 0 : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(body));
}
