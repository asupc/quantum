// ============================================================================
// ACME 自动签证书（Let's Encrypt + 阿里云云解析 DNS-01 校验）
//
// 干什么：定时检查证书剩余有效期，临期（默认 3 天）则自动走 ACME 签一张新证书，
//         本地校验无误后写入受控证书目录，最后重启 nginx 容器加载新证书。
// 运行方式：任务管理 → 新建任务 → 执行脚本选 acme_cert.cs → 定时执行填 Cron
//           （如 0 15 3 * * ? 每日 03:15）→ 强制结束时间设 ≥ 15 分钟（DNS 生效等待占大头）。
//
// 需配置的环境变量（缺失即抛错，脚本不内置任何密钥缺省值）：
//   AliAccessKeyId / AliAccessKeySecret  必填  阿里云 RAM 子账号 AK，仅需云解析读写权限
//   AcmeDomains                          必填  证书 SAN 列表，逗号分隔，支持泛域名
//                                              例：example.com,*.example.com
//   AcmeCertDir                          可选  证书落盘子目录，默认 ssl —— 相对下载根目录
//                                              （appsettings Quantum:FileDownloadRoot），禁绝对路径与 ..
//   AcmeContainers                       可选  续签成功后重启的容器名，逗号分隔，默认 nginx；none = 不重启
//   AcmeRenewDays                        可选  临期阈值（天），默认 3（建议 30，理由见计划文档风险项）
//   AcmeCaDirectory                      可选  ACME directory URL，默认 Let's Encrypt 生产；联调建议先指 staging
//   AcmeDnsWaitSeconds                   可选  TXT 写入后等待生效的秒数，默认 60
//   AcmeDnsProbeSeconds                  可选  单个标识符校验轮询的最长秒数，默认 180
//   AcmeCleanupTxt                       可选  默认 1（收尾删除 TXT）；0 = 保留（排障用）
// 脚本自维护（首跑自动写入，勿手工编辑）：
//   AcmeAccountKey                       ACME 账号私钥（PKCS#8 DER 的 base64），删除即重新注册账号
//   AcmeLedger                           到期台账 JSON（不含任何密钥材料），删除即强制执行一次续签
//
// 落盘产物：privkey.pem / cert.pem / chain.pem / fullchain.pem（同名覆盖，先写临时文件再原子替换）。
// DNS-01 记录值：发布 base64url(SHA-256(keyAuthorization))（43 字符摘要），与 acme.sh 3.x 默认一致；
//   实测 Let's Encrypt 对 RFC 8555 原文那套「直接放 keyAuthorization」会判 Incorrect TXT record。
// 私钥只落盘，不入库、不进日志、不进通知；日志与通知只出现相对路径、字节数、指纹与到期时间。
// 通知（受任务「是否推送」开关 EnablePush 控制）：成功发摘要（域名/到期/目录/文件/容器结果），
// 失败也发一条（阶段/域名/现网证书是否已被改动/原因）；被强制结束时间取消时不发通知（属主动终止）。
// 部署前提：证书目录须与 nginx 容器共享同一宿主目录，见 docs/ACME自动签证书与nginx证书续期实施计划.md 批次 3。
// 预期门禁告警：hardcoded-url 两条（LE 与云解析的公网端点，属任务固有公开地址，按脱敏规则保留为常量）。
// ============================================================================
using System.Formats.Asn1;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class AcmeCertTask : IQuantumTask
{
    /// <summary>Let's Encrypt 生产 ACME directory（可用环境变量 AcmeCaDirectory 切 staging）。</summary>
    private const string DefaultCaDirectory = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>阿里云云解析 RPC 端点与 API 版本（V2 签名，HMAC-SHA1）。</summary>
    private const string AliDnsEndpoint = "https://alidns.aliyuncs.com/";

    private const string AliDnsVersion = "2015-01-09";

    /// <summary>ACME JWS 的媒体类型：必须不带 charset 参数（LE 严格校验）。</summary>
    private const string JoseJsonMediaType = "application/jose+json";

    /// <summary>脚本自维护的两个环境变量名。</summary>
    private const string AccountKeyEnv = "AcmeAccountKey";

    private const string LedgerEnv = "AcmeLedger";

    private const string SubjectAltNameOid = "2.5.29.17";

    /// <summary>签发后本地校验的最低剩余有效期（天）：低于此值说明 CA 返回异常，拒绝落盘。</summary>
    private const int MinIssuedValidityDays = 30;

    private QuantumTaskContext _ctx;
    private CancellationToken _ct;

    private string _accessKeyId;
    private string _accessKeySecret;

    private string _nonce;
    private string _newNonceUrl;
    private string _accountUrl;
    private string _orderUrl;
    private string _thumbprint;
    private RSA _accountKey;
    private JObject _directory;

    /// <summary>本次执行写入/改动的 TXT 记录 Id（收尾按此清理，重跑命中同值则复用不新增）。</summary>
    private readonly List<string> _touchedRecordIds = new();

    /// <summary>ACME 响应：JSON 延迟解析——证书端点返回的是 PEM，过早按 JSON 解会误报。</summary>
    private sealed class AcmeResponse
    {
        public string Text { get; init; }
        public string Location { get; init; }

        private JObject _json;

        public JObject Json => _json ??= ParseJson(Text, "ACME 接口");
    }

    private sealed record JwsResult(bool Ok, AcmeResponse Response, string Problem);

    private sealed record AliResponse(bool Ok, JObject Json, string Code, string Message);

    // ---- 失败通知用的现场信息（各阶段推进时更新，异常发生处即最后写入的那个） ----
    private List<string> _domains = new();
    private string _certDir = "ssl";
    private string _stage = "配置读取";
    private bool _filesWritten;

    /// <summary>外壳只负责一件事：任何非取消异常都要变成一条失败通知后再原样上抛
    /// （任务执行记录仍按失败收，日志里有完整堆栈）。取消是运维主动终止，不该报警。</summary>
    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        _ct = ct;
        try
        {
            await RunCoreAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await NotifyFailureAsync(e);
            throw;
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ctx = _ctx;

        // ---------------------------------------------------------------- 1. 配置读取
        _accessKeyId = Require("AliAccessKeyId");
        _accessKeySecret = Require("AliAccessKeySecret");
        var domains = ReadDomains(Require("AcmeDomains"));
        var certDir = Optional("AcmeCertDir", "ssl");
        _domains = domains;
        _certDir = certDir;
        var renewDays = OptionalInt("AcmeRenewDays", 3, 0);
        var caDirectory = Optional("AcmeCaDirectory", DefaultCaDirectory);
        var containers = ReadList(Optional("AcmeContainers", "nginx"));
        var dnsWaitSeconds = OptionalInt("AcmeDnsWaitSeconds", 60, 0);
        var cleanupTxt = Optional("AcmeCleanupTxt", "1") != "0";

        ctx.Log($"证书 SAN：{string.Join("、", domains)}");
        ctx.Log($"落盘子目录：{certDir}（相对下载根目录）；临期阈值：{renewDays} 天；" +
                $"CA：{caDirectory}；重启容器：{(containers.Count == 0 ? "（不配置则不重启）" : string.Join("、", containers))}");

        // ---------------------------------------------------------------- 2. 到期台账：未临期直接收工（避免撞 CA 速率限制）
        _stage = "读取到期台账";
        var ledger = await ReadLedgerAsync();
        if (ledger != null && !NeedRenew(ledger, domains, renewDays))
        {
            return;
        }
        if (ledger == null)
        {
            ctx.Log($"无到期台账（环境变量 {LedgerEnv} 为空），按首次签发处理。");
        }

        // TXT 记录无论成功失败都要清理，否则下次 AddDomainRecord 会撞 DomainRecordDuplicate
        try
        {
            // ------------------------------------------------------------ 3. ACME 账号（密钥复用，避免重复注册）
            _stage = "ACME 账号注册";
            await LoadOrCreateAccountKeyAsync();
            await LoadDirectoryAsync(caDirectory);
            _accountUrl = await EnsureAccountAsync();
            ctx.Log($"ACME 账号：{_accountUrl}");
            await SelfCheckAccountKeyAsync();

            // ------------------------------------------------------------ 4. 下单 + 阿里云 DNS-01 校验
            _stage = "创建订单";
            var order = await PostJwsAsync(_directory["newOrder"].Value<string>(), new JObject
            {
                ["identifiers"] = new JArray(domains.Select(name => new JObject
                {
                    ["type"] = "dns",
                    ["value"] = name
                }))
            }.ToString(Formatting.None), useJwk: false);
            _orderUrl = order.Location ?? throw new Exception("ACME 订单响应缺少 Location 头。");
            var authUrls = ((order.Json["authorizations"] as JArray) ?? new JArray())
                .Select(node => node.Value<string>()).Where(url => !string.IsNullOrEmpty(url)).ToList();
            var finalizeUrl = order.Json["finalize"]?.Value<string>()
                              ?? throw new Exception("ACME 订单响应缺少 finalize 端点。");
            ctx.Log($"订单已创建（状态 {order.Json["status"]?.Value<string>() ?? "未知"}），待校验标识符 {authUrls.Count} 个。");

            var pending = new List<(string AuthUrl, string ChallengeUrl, string Record, string Token)>();
            _stage = "云解析写入 TXT 记录";
            foreach (var authUrl in authUrls)
            {
                pending.AddRange(await PrepareDnsChallengeAsync(authUrl));
            }

            if (pending.Count > 0)
            {
                _stage = "CA 校验 DNS-01 挑战";
                ctx.Log($"TXT 记录已就位，等待公网生效 {dnsWaitSeconds}s…");
                await Task.Delay(TimeSpan.FromSeconds(dnsWaitSeconds), _ct);
                foreach (var item in pending)
                {
                    await PostJwsAsync(item.ChallengeUrl, "{}", useJwk: false);
                }
                foreach (var item in pending)
                {
                    await WaitAuthorizedAsync(item.AuthUrl, item.Record, item.Token);
                }
            }
            else
            {
                ctx.Log("全部标识符此前已授权，直接续签。");
            }

            // ------------------------------------------------------------ 5. 出 CSR → finalize → 取证书链
            _stage = "签发并取回证书（finalize）";
            using var certKey = RSA.Create(2048);
            var finalized = await PostJwsAsync(finalizeUrl, new JObject { ["csr"] = Base64Url(BuildCsr(domains, certKey)) }
                .ToString(Formatting.None), useJwk: false);
            var certUrl = await WaitCertificateUrlAsync(finalized);
            var chainText = (await PostJwsAsync(certUrl, "", useJwk: false)).Text;
            var blocks = SplitPemBlocks(chainText);
            if (blocks.Count == 0)
            {
                throw new Exception("CA 返回的证书内容为空（PEM 未解析出任何证书块），终止且不覆盖现有证书。");
            }
            if (blocks.Count < 2)
            {
                ctx.Log($"警告：CA 只返回了 {blocks.Count} 段证书，通常应为「叶子 + 中间证书」，请留意 nginx 链完整性。");
            }

            var leafPem = blocks[0];
            var chainPem = string.Concat(blocks.Skip(1));
            var fullChainPem = leafPem + chainPem;
            _stage = "本地校验证书（私钥配对/有效期/SAN）";
            var (notAfter, thumbprint) = VerifyIssued(leafPem, chainPem, domains, certKey);

            // ------------------------------------------------------------ 6. 落盘（校验通过才写盘，写盘失败不重启）
            _stage = "写入证书文件";
            var files = await WriteCertFilesAsync(certDir, certKey.ExportPkcs8PrivateKeyPem(), leafPem, chainPem, fullChainPem);
            _filesWritten = true;

            // ------------------------------------------------------------ 7. 台账（不含密钥材料）
            _stage = "写入到期台账";
            await SaveLedgerAsync(domains, certDir, notAfter, thumbprint, files);

            // ------------------------------------------------------------ 8. 重启容器加载新证书并复核
            _stage = "重启服务容器";
            var restartResults = await RestartContainersAsync(containers);

            // ------------------------------------------------------------ 9. 通知摘要
            await SendSummaryAsync(domains, certDir, notAfter, files, restartResults);
        }
        finally
        {
            try
            {
                await CleanupTxtRecordsAsync(cleanupTxt);
            }
            catch (Exception e)
            {
                // 清理失败不掩盖主异常（执行被强制终止时也走这里，此时不该再抛）
                _ctx.Log($"TXT 记录清理未完成，请到云解析手动删除 _acme-challenge 记录：{e.Message}");
            }
        }
    }

    // ==================================================================== 配置与环境变量

    private string Require(string name)
    {
        if (_ctx.Variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }
        throw new Exception($"缺少环境变量 {name}，请先在环境变量页配置。");
    }

    private string Optional(string name, string fallback)
    {
        return _ctx.Variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
    }

    private int OptionalInt(string name, int fallback, int min)
    {
        var text = Optional(name, null);
        if (text != null)
        {
            if (int.TryParse(text, out var value) && value >= min)
            {
                return value;
            }
            _ctx.Log($"环境变量 {name}={text} 不是 ≥{min} 的整数，按默认值 {fallback} 执行。");
        }
        return fallback;
    }

    private List<string> ReadDomains(string raw)
    {
        var names = ReadList(raw)
            .Select(name => name.TrimEnd('.'))
            .Where(name => name.Length > 0)
            .Select(ToAscii)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            throw new Exception("环境变量 AcmeDomains 未解析出任何域名。");
        }
        foreach (var name in names)
        {
            var probe = name.StartsWith("*.") ? name[2..] : name;
            if (!probe.Contains('.') || probe.StartsWith(".") || probe.EndsWith("."))
            {
                throw new Exception($"AcmeDomains 中的「{name}」不是合法域名（应形如 example.com 或 *.example.com）。");
            }
        }
        return names;
    }

    private static List<string> ReadList(string raw)
    {
        return (raw ?? string.Empty)
            .Split([',', '，', ';', '；', '、', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToList();
    }

    private async Task<string> ReadEnvAsync(string name)
    {
        var rows = await _ctx.Env.QueryAsync(name, ct: _ct);
        return rows.FirstOrDefault(n => n.Enabled)?.Value;
    }

    /// <summary>中文域名转 punycode：云解析只收 punycode，纯 ASCII 原样返回
    /// （IdnMapping 对含下划线的主机记录名会抛 ArgumentException，故只碰非 ASCII 输入）。</summary>
    private static string ToAscii(string host)
    {
        if (host.All(ch => ch < 128))
        {
            return host;
        }
        try
        {
            return new IdnMapping().GetAscii(host);
        }
        catch (ArgumentException)
        {
            return host;
        }
    }

    // ==================================================================== ACME 账号与协议

    /// <summary>账号密钥存在即复用（重复注册会白占 CA 的注册配额），不存在则生成并写回环境变量。</summary>
    private async Task LoadOrCreateAccountKeyAsync()
    {
        var stored = await ReadEnvAsync(AccountKeyEnv);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                var key = RSA.Create();
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.Trim()), out _);
                _accountKey = key;
                _thumbprint = ComputeThumbprint();
                _ctx.Log($"复用环境变量 {AccountKeyEnv} 中的 ACME 账号密钥（指纹 {_thumbprint}）。");
                return;
            }
            catch (Exception e) when (e is FormatException || e is CryptographicException)
            {
                _ctx.Log($"环境变量 {AccountKeyEnv} 内容无法解析（{e.Message}），将重新生成账号密钥。");
            }
        }

        _accountKey = RSA.Create(2048);
        _thumbprint = ComputeThumbprint();
        await _ctx.Env.SaveAsync(AccountKeyEnv, Convert.ToBase64String(_accountKey.ExportPkcs8PrivateKey()),
            "ACME 账号私钥（PKCS#8 DER 的 base64）。删除该变量会让下次执行重新注册账号，请勿随意清理。", true, _ct);
        _ctx.Log($"已生成新的 ACME 账号密钥并写入环境变量 {AccountKeyEnv}（指纹 {_thumbprint}）。");
    }

    /// <summary>RFC 7638 JWK 指纹：成员按字典序 e/kty/n 拼紧凑 JSON，SHA-256 后 base64url。</summary>
    private static string ThumbprintFromJwk(string e, string kty, string n)
    {
        var canonical = $"{{\"e\":\"{e}\",\"kty\":\"{kty}\",\"n\":\"{n}\"}}";
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private string ComputeThumbprint()
    {
        var parameters = _accountKey.ExportParameters(false);
        return ThumbprintFromJwk(Base64Url(parameters.Exponent), "RSA", Base64Url(parameters.Modulus));
    }

    private JObject AccountJwk()
    {
        var parameters = _accountKey.ExportParameters(false);
        return new JObject
        {
            ["kty"] = "RSA",
            ["n"] = Base64Url(parameters.Modulus),
            ["e"] = Base64Url(parameters.Exponent)
        };
    }

    private async Task LoadDirectoryAsync(string caDirectory)
    {
        using var response = await _ctx.Http.GetAsync(caDirectory, _ct);
        var text = await response.Content.ReadAsStringAsync(_ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"读取 ACME directory 失败：HTTP {(int)response.StatusCode} {Truncate(text)}");
        }
        _directory = ParseJson(text, "ACME directory");
        _newNonceUrl = Pick(_directory, "newNonce");
        Pick(_directory, "newAccount");
        Pick(_directory, "newOrder");
        _nonce = await FetchNonceAsync(_newNonceUrl);
        _ctx.Log("已读取 ACME directory 并取得初始 nonce。");
    }

    private static string Pick(JObject directory, string member)
    {
        return directory[member]?.Value<string>()
               ?? throw new Exception($"ACME directory 缺少 {member} 端点，请确认 AcmeCaDirectory 指向标准 ACME 服务。");
    }

    private async Task<string> FetchNonceAsync(string url)
    {
        using var response = await _ctx.Http.GetAsync(url, _ct);
        if (response.Headers.TryGetValues("Replay-Nonce", out var values))
        {
            return values.First();
        }
        throw new Exception($"未能从 CA 取得 Replay-Nonce（HTTP {(int)response.StatusCode}），请检查出网连通性与 AcmeCaDirectory。");
    }

    private async Task<string> EnsureAccountAsync()
    {
        var newAccount = Pick(_directory, "newAccount");
        try
        {
            var created = await PostJwsAsync(newAccount, new JObject { ["termsOfServiceAgreed"] = true }
                .ToString(Formatting.None), useJwk: true);
            return created.Location ?? throw new Exception("ACME 账号响应缺少 Location 头（无法取得账号地址）。");
        }
        catch (Exception e) when (e.Message.Contains("accountKeyPresent", StringComparison.OrdinalIgnoreCase)
                                         || e.Message.Contains("already registered", StringComparison.OrdinalIgnoreCase))
        {
            var existing = await PostJwsAsync(newAccount, new JObject { ["onlyReturnExisting"] = true }
                .ToString(Formatting.None), useJwk: true);
            return existing.Location ?? throw new Exception("ACME 已存在账号但响应缺少 Location 头。");
        }
    }

    /// <summary>账号自检：向 CA 取回它登记的账号公钥，按同一套 RFC 7638 规则再算一次指纹与本地对撞。
    /// DNS-01 的期望值是「CA 存的这把公钥」算出来的，一旦本地私钥与注册用的公钥脱钩
    /// （AcmeAccountKey 被换过/被同名多行 & 拼接污染），校验必然报 Incorrect TXT record——
    /// 与其白跑一轮 DNS 写入 + 60s 等待，不如在这里直接挡下并说清怎么办。</summary>
    private async Task SelfCheckAccountKeyAsync()
    {
        var account = (await PostJwsAsync(_accountUrl, "", useJwk: false)).Json;
        var key = account?["key"] as JObject;
        var kty = key?["kty"]?.Value<string>();
        var e = key?["e"]?.Value<string>();
        var n = key?["n"]?.Value<string>();
        if (string.IsNullOrEmpty(e) || string.IsNullOrEmpty(n) || string.IsNullOrEmpty(kty))
        {
            _ctx.Log("账号自检：CA 未返回账号公钥（该 CA 行为差异），跳过指纹对撞。");
            return;
        }
        var serverThumbprint = ThumbprintFromJwk(e, kty, n);
        if (serverThumbprint != _thumbprint)
        {
            throw new Exception($"账号公钥指纹与本地私钥不一致（CA 登记 {serverThumbprint} ≠ 本地 {_thumbprint}）：" +
                                $"环境变量 {AccountKeyEnv} 里的私钥不是注册该 ACME 账号时用的那把，DNS-01 不可能通过。" +
                                $"请删除该环境变量让脚本重新生成密钥并注册新账号（注意注册配额）。");
        }
        _ctx.Log($"账号自检通过：CA 登记的公钥指纹与本地一致（{_thumbprint}）。");
    }

    /// <summary>ACME 的 JWS 请求：payloadJson 传 "" 即 POST-as-GET，传 "{}" 即空对象。</summary>
    private async Task<AcmeResponse> PostJwsAsync(string url, string payloadJson, bool useJwk)
    {
        var first = await TryPostJwsAsync(url, payloadJson, useJwk);
        if (first.Ok)
        {
            return first.Response;
        }
        // nonce 是一次性的：上一轮取消/重试会消费掉缓存值，badNonce 换新 nonce 再试一次
        if (!first.Problem.Contains("badNonce", StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception(first.Problem);
        }
        _ctx.Log("Replay-Nonce 已失效，重新获取后重试一次。");
        _nonce = await FetchNonceAsync(_newNonceUrl);
        var second = await TryPostJwsAsync(url, payloadJson, useJwk);
        if (!second.Ok)
        {
            throw new Exception(second.Problem);
        }
        return second.Response;
    }

    private async Task<JwsResult> TryPostJwsAsync(string url, string payloadJson, bool useJwk)
    {
        var header = new JObject
        {
            ["alg"] = "RS256",
            ["nonce"] = _nonce,
            ["url"] = url
        };
        if (useJwk)
        {
            header["jwk"] = AccountJwk();
        }
        else
        {
            header["kid"] = _accountUrl;
        }

        var protectedPart = Base64Url(Encoding.UTF8.GetBytes(header.ToString(Formatting.None)));
        var payloadPart = Base64Url(Encoding.UTF8.GetBytes(payloadJson ?? string.Empty));
        var signature = Base64Url(_accountKey.SignData(Encoding.ASCII.GetBytes($"{protectedPart}.{payloadPart}"),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var jws = new JObject
        {
            ["protected"] = protectedPart,
            ["payload"] = payloadPart,
            ["signature"] = signature
        };

        // Content-Type 必须**恰好**是 application/jose+json：StringContent(json, Encoding.UTF8, ...) 会附加
        // "; charset=utf-8"，Let's Encrypt 直接判 malformed（实测踩过），所以手搭 ByteArrayContent 只给媒体类型。
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(jws.ToString(Formatting.None)));
        content.Headers.ContentType = new MediaTypeHeaderValue(JoseJsonMediaType);
        using var response = await _ctx.Http.PostAsync(url, content, _ct);
        if (response.Headers.TryGetValues("Replay-Nonce", out var nonceValues))
        {
            _nonce = nonceValues.First();
        }
        var text = await response.Content.ReadAsStringAsync(_ct);
        if (response.IsSuccessStatusCode)
        {
            return new JwsResult(true, new AcmeResponse { Text = text, Location = ResolveLocation(response, url) }, null);
        }
        return new JwsResult(false, null, DescribeProblem(text, (int)response.StatusCode, url));
    }

    private static string ResolveLocation(HttpResponseMessage response, string requestUrl)
    {
        var location = response.Headers.Location;
        if (location == null)
        {
            return null;
        }
        return location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(requestUrl), location).ToString();
    }

    private static string DescribeProblem(string text, int status, string url)
    {
        var brief = Truncate(text.Replace("\r", " ").Replace("\n", " "));
        try
        {
            var problem = JsonConvert.DeserializeObject<JObject>(text);
            var detail = problem?["detail"]?.Value<string>();
            var type = problem?["type"]?.Value<string>();
            if (!string.IsNullOrEmpty(detail))
            {
                return $"ACME 调用失败（HTTP {status}{(string.IsNullOrEmpty(type) ? "" : $"，{type}")}）：{detail}";
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体（网关/代理返回的 HTML），走下面的兜底文案
        }
        return $"ACME 调用失败（HTTP {status}）：{brief}（{url}）";
    }

    // ==================================================================== 签发五步：授权 → 校验 → finalize → 取证书

    /// <summary>取一个标识符的 dns-01 挑战并在云解析落 TXT；已授权（valid）的返回空表。</summary>
    private async Task<List<(string AuthUrl, string ChallengeUrl, string Record, string Token)>> PrepareDnsChallengeAsync(string authUrl)
    {
        var auth = (await PostJwsAsync(authUrl, "", useJwk: false)).Json;
        var domain = auth["identifier"]?["value"]?.Value<string>() ?? "未知标识符";
        var status = auth["status"]?.Value<string>();
        if (status == "valid")
        {
            _ctx.Log($"标识符 {domain} 此前已授权（有效窗口内复用），跳过校验。");
            return new List<(string, string, string, string)>();
        }

        var challenge = ((auth["challenges"] as JArray) ?? new JArray())
            .FirstOrDefault(node => node["type"]?.Value<string>() == "dns-01");
        var token = challenge?["token"]?.Value<string>();
        var challengeUrl = challenge?["url"]?.Value<string>();
        if (token == null || challengeUrl == null)
        {
            throw new Exception($"标识符 {domain} 没有可用的 dns-01 挑战，无法自动校验。");
        }

        // 泛域名 *.example.com 的挑战记录名仍是 _acme-challenge.example.com
        var host = domain.StartsWith("*.") ? domain[2..] : domain;
        var keyAuthorization = $"{token}.{_thumbprint}";
        // 落 DNS 的是 keyAuthorization 的 SHA-256 摘要（base64url，43 字符），不是原文：
        // 实测 Let's Encrypt 对原始 keyAuthorization 会判「Incorrect TXT record」，
        // 而 acme.sh 3.x 默认就是发布这个摘要并签发成功（见其 _digest "sha256" 分支）。
        var txtValue = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(keyAuthorization)));
        var (zone, rr) = await ResolveZoneAsync($"_acme-challenge.{host}");
        await EnsureTxtRecordAsync(zone, rr, txtValue);
        _ctx.Log($"标识符 {domain}：TXT 记录 {rr}.{zone} = {txtValue}（keyAuthorization 的 SHA-256 摘要）");
        return new List<(string, string, string, string)> { (authUrl, challengeUrl, $"{rr}.{zone}", token) };
    }

    private async Task WaitAuthorizedAsync(string authUrl, string record, string token)
    {
        var timeoutSeconds = OptionalInt("AcmeDnsProbeSeconds", 180, 30);
        var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        while (DateTime.Now < deadline)
        {
            _ct.ThrowIfCancellationRequested();
            var auth = (await PostJwsAsync(authUrl, "", useJwk: false)).Json;
            var status = auth["status"]?.Value<string>();
            switch (status)
            {
                case "valid":
                    _ctx.Log($"{record} 校验通过。");
                    return;
                case "invalid":
                    // 授权原文一并打出：Boulder 可能在此带出它实际查询的名字/validationRecord，
                    // 只摘 detail 会丢掉这些定位信息
                    throw new Exception($"{record} 校验失败：{DescribeAuthErrors(auth)}\n{TokenDriftHint(auth, token)}\n" +
                                        $"授权原文：{BriefJson(auth)}");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), _ct);
        }
        throw new Exception($"{record} 校验超时（{timeoutSeconds}s）。" +
                                    "请确认 TXT 已公网生效（dig +short _acme-challenge.域名 TXT）、该域名是否在当前 AK 所属账号下。");
    }

    /// <summary>失败取证：把 CA 当前登记的 dns-01 token 与本次写入用的 token 对撞。
    /// 不一致 = 授权对象被复用/轮换，我们写的是旧 token 的记录——属客户端流程问题，
    /// 必须按新 token 重写再触发；一致 = 排除 token 漂移，才轮得到 DNS 视图/账号密钥那两个方向。
    /// 这个判据在失败响应里本来就带着，不用额外发请求。</summary>
    private static string TokenDriftHint(JObject auth, string token)
    {
        var current = ((auth["challenges"] as JArray) ?? new JArray())
            .FirstOrDefault(node => node["type"]?.Value<string>() == "dns-01")?["token"]?.Value<string>();
        if (string.IsNullOrEmpty(current))
        {
            return "token 对撞：CA 未回 challenges 的 token，无法判定。";
        }
        return current == token
            ? $"token 对撞：一致（{current}），排除 token 漂移。"
            : $"token 对撞：**已漂移**——本次写入用的是 {token}，而 CA 现在登记的是 {current}；" +
              "须按新 token 重写 TXT 后再触发校验（不是 DNS 传播问题）。";
    }

    /// <summary>把 JSON 压成单行摘要（授权原文里含换行与冗长字段，日志里只留可读的前若干字符）。</summary>
    private static string BriefJson(JToken token, int max = 700)
    {
        var text = (token?.ToString(Formatting.None) ?? "null").Replace("\r", " ").Replace("\n", " ");
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string DescribeAuthErrors(JObject auth)
    {
        var parts = new List<string>();
        var error = auth["error"];
        if (error?["detail"] != null)
        {
            parts.Add(error["detail"].Value<string>());
        }
        foreach (var challenge in ((auth["challenges"] as JArray) ?? new JArray()))
        {
            var detail = challenge["error"]?["detail"]?.Value<string>();
            if (!string.IsNullOrEmpty(detail))
            {
                parts.Add($"{challenge["type"]?.Value<string>()}：{detail}");
            }
        }
        return parts.Count > 0 ? string.Join("；", parts) : "CA 未给出原因（status=invalid）";
    }

    private async Task<string> WaitCertificateUrlAsync(AcmeResponse finalized)
    {
        var certUrl = finalized.Json["certificate"]?.Value<string>();
        if (!string.IsNullOrEmpty(certUrl))
        {
            return certUrl;
        }
        var deadline = DateTime.Now.AddSeconds(120);
        while (DateTime.Now < deadline)
        {
            _ct.ThrowIfCancellationRequested();
            var order = (await PostJwsAsync(_orderUrl, "", useJwk: false)).Json;
            certUrl = order["certificate"]?.Value<string>();
            if (!string.IsNullOrEmpty(certUrl))
            {
                return certUrl;
            }
            var status = order["status"]?.Value<string>();
            if (status == "invalid")
            {
                throw new Exception($"ACME 订单被拒绝：{DescribeOrderErrors(order)}");
            }
            _ctx.Log($"订单状态 {status ?? "未知"}，5s 后重查…");
            await Task.Delay(TimeSpan.FromSeconds(5), _ct);
        }
        throw new Exception("证书签发超时（finalize 后 120s 未返回证书地址）。");
    }

    private static string DescribeOrderErrors(JObject order)
    {
        var messages = ((order["errors"] as JArray) ?? new JArray())
            .Select(error => error["detail"]?.Value<string>())
            .Where(text => !string.IsNullOrEmpty(text));
        var joined = string.Join("；", messages);
        return string.IsNullOrEmpty(joined) ? "CA 未给出原因" : joined;
    }

    private static byte[] BuildCsr(List<string> domains, RSA certKey)
    {
        // CN 取第一个非泛域名（无则退回首项）；真正生效的是 SAN 扩展
        var commonName = domains.FirstOrDefault(name => !name.StartsWith("*.")) ?? domains[0];
        var request = new CertificateRequest(new X500DistinguishedName($"CN={commonName}"), certKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var domain in domains)
        {
            san.AddDnsName(domain);
        }
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSigningRequest();
    }

    // ==================================================================== 证书校验与落盘

    /// <summary>写盘前的强制校验：私钥配对、剩余有效期、SAN 覆盖；链构建失败只告警
    /// （容器里没有 ISRG 根证书属正常现象，不代表证书有问题）。返回叶子证书到期时间与指纹。</summary>
    private (DateTime NotAfter, string Thumbprint) VerifyIssued(string leafPem, string chainPem, List<string> domains, RSA certKey)
    {
        using var leaf = X509Certificate2.CreateFromPem(leafPem);

        var certModulus = leaf.GetRSAPublicKey()?.ExportParameters(false).Modulus;
        if (certModulus == null || !certModulus.AsSpan().SequenceEqual(certKey.ExportParameters(false).Modulus))
        {
            throw new Exception("签发的证书公钥与生成的私钥不配对，拒绝落盘。");
        }

        var remaining = leaf.NotAfter - DateTime.Now;
        if (remaining < TimeSpan.FromDays(MinIssuedValidityDays))
        {
            throw new Exception($"签发的证书剩余有效期仅 {remaining.TotalDays:F0} 天（低于 {MinIssuedValidityDays} 天），异常，拒绝落盘。");
        }
        if (leaf.NotBefore > DateTime.Now.AddMinutes(5))
        {
            throw new Exception($"证书尚未生效（NotBefore={leaf.NotBefore:yyyy-MM-dd HH:mm}），请检查服务器时钟与 NTP。");
        }

        var sanNames = ReadDnsNames(leaf);
        var missing = domains.Where(domain => !sanNames.Any(name => string.Equals(name, domain, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
        {
            throw new Exception($"证书 SAN 未覆盖 {string.Join("、", missing)}，拒绝落盘（实际 SAN：{string.Join("、", sanNames)}）。");
        }

        var extras = new List<X509Certificate2>();
        using var chain = new X509Chain
        {
            ChainPolicy = { RevocationMode = X509RevocationMode.NoCheck }
        };
        foreach (var intermediate in SplitPemBlocks(chainPem))
        {
            var cert = X509Certificate2.CreateFromPem(intermediate);
            extras.Add(cert);
            chain.ChainPolicy.ExtraStore.Add(cert);
        }
        var built = chain.Build(leaf);
        foreach (var cert in extras)
        {
            cert.Dispose();
        }
        if (!built)
        {
            var reason = chain.ChainStatus.Length > 0 ? chain.ChainStatus[0].StatusInformation : "未知原因";
            _ctx.Log($"警告：本机无法把链构建到受信根（{reason}）。容器缺 ISRG 根证书时属正常，若为自签/内网 CA 请人工确认。");
        }

        _ctx.Log($"证书校验通过：到期 {leaf.NotAfter:yyyy-MM-dd HH:mm}（剩余 {remaining.TotalDays:F0} 天），" +
                 $"SAN={string.Join("、", sanNames)}，链{(built ? "完整" : "待人工确认")}，指纹 {leaf.Thumbprint}");
        return (leaf.NotAfter, leaf.Thumbprint);
    }

    /// <summary>解 SAN 扩展里的 dNSName 列表（.NET 未提供公开枚举 API，按 DER 手解）。</summary>
    private static List<string> ReadDnsNames(X509Certificate2 cert)
    {
        var names = new List<string>();
        var extension = cert.Extensions.FirstOrDefault(item => item.Oid?.Value == SubjectAltNameOid);
        if (extension == null)
        {
            return names;
        }
        var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var dnsNameTag = new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: false);
        while (sequence.HasData)
        {
            // GeneralName 是 CHOICE：只取 [2] dNSName，其余（URI/IP/邮件）整段跳过
            if (sequence.PeekTag() == dnsNameTag)
            {
                names.Add(Encoding.UTF8.GetString(ReadContent(sequence.ReadEncodedValue().ToArray())));
            }
            else
            {
                sequence.ReadEncodedValue();
            }
        }
        return names;
    }

    /// <summary>从 DER TLV 中取内容字节（处理短/长两种长度形式）。</summary>
    private static byte[] ReadContent(byte[] tlv)
    {
        if ((tlv[1] & 0x80) == 0)
        {
            return tlv[2..(2 + tlv[1])];
        }
        var lengthBytes = tlv[1] & 0x7F;
        var length = 0;
        for (var i = 0; i < lengthBytes; i++)
        {
            length = (length << 8) | tlv[2 + i];
        }
        var offset = 2 + lengthBytes;
        return tlv[offset..(offset + length)];
    }

    /// <summary>按 PEM 块切分证书链（以 -----END 收尾为完整块，尾部有散行说明内容被截断）。</summary>
    private static List<string> SplitPemBlocks(string pem)
    {
        var blocks = new List<string>();
        var current = new StringBuilder();
        foreach (var rawLine in (pem ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            current.Append(line).Append('\n');
            if (line.StartsWith("-----END "))
            {
                blocks.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
        {
            throw new Exception("证书 PEM 结尾不完整（缺少 END 标记），拒绝落盘。");
        }
        return blocks;
    }

    private async Task<List<string>> WriteCertFilesAsync(string certDir, string privKeyPem, string leafPem, string chainPem, string fullChainPem)
    {
        if (_ctx.File == null)
        {
            throw new Exception("当前后端未提供 ctx.File 文本落盘能力（SaveTextAsync），请部署批次 1 的后端版本。");
        }
        var targets = new (string Name, string Content)[]
        {
            ("privkey.pem", privKeyPem),
            ("cert.pem", leafPem),
            ("chain.pem", chainPem),
            ("fullchain.pem", fullChainPem)
        };
        var files = new List<string>();
        foreach (var (name, content) in targets)
        {
            var saved = await _ctx.File.SaveTextAsync(content, name, certDir, _ct);
            files.Add(saved.RelativePath);
            // 只打路径与字节数：证书内容不入日志（私钥尤其致命）
            _ctx.Log($"已写入 {saved.RelativePath}（{saved.Length} 字节）→ {saved.FullPath}");
        }
        return files;
    }

    // ==================================================================== 到期台账

    private async Task<JObject> ReadLedgerAsync()
    {
        var raw = await ReadEnvAsync(LedgerEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        try
        {
            return JsonConvert.DeserializeObject<JObject>(raw);
        }
        catch (JsonException e)
        {
            _ctx.Log($"环境变量 {LedgerEnv} 不是合法 JSON（{e.Message}），按无台账处理并强制续签。");
            return null;
        }
    }

    private bool NeedRenew(JObject ledger, List<string> domains, int renewDays)
    {
        var lastDomains = ((ledger["domains"] as JArray) ?? new JArray())
            .Select(node => node.Value<string>())
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!lastDomains.SequenceEqual(domains.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            _ctx.Log($"台账域名（{string.Join("、", lastDomains)}）与当前 AcmeDomains 不一致，强制续签。");
            return true;
        }
        if (!DateTime.TryParse(ledger["notAfter"]?.Value<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var notAfter))
        {
            _ctx.Log("台账缺少可解析的到期时间，强制续签。");
            return true;
        }
        var left = notAfter - DateTime.Now;
        if (left > TimeSpan.FromDays(renewDays))
        {
            _ctx.Log($"现网证书 {notAfter:yyyy-MM-dd HH:mm} 到期（剩余 {left.TotalDays:F0} 天），未到 {renewDays} 天阈值，本次跳过。" +
                     $"如需强制续签：删除环境变量 {LedgerEnv} 或临时调大 AcmeRenewDays。");
            return false;
        }
        _ctx.Log($"证书将在 {left.TotalDays:F1} 天后到期（阈值 {renewDays} 天），开始续签。");
        return true;
    }

    private async Task SaveLedgerAsync(List<string> domains, string certDir, DateTime notAfter, string thumbprint, List<string> files)
    {
        var ledger = new JObject
        {
            ["domains"] = new JArray(domains),
            ["dir"] = certDir,
            ["notAfter"] = notAfter.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["issuedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["thumbprint"] = thumbprint,
            ["files"] = new JArray(files)
        };
        await _ctx.Env.SaveAsync(LedgerEnv, ledger.ToString(Formatting.None),
            $"ACME 证书到期台账（不含密钥）。到期 {notAfter:yyyy-MM-dd}；删除本变量可强制执行一次续签", true, _ct);
        _ctx.Log($"台账已写入环境变量 {LedgerEnv}。");
    }

    // ==================================================================== 阿里云云解析（V2 RPC 签名）

    private async Task<(string Zone, string Rr)> ResolveZoneAsync(string recordName)
    {
        var labels = recordName.Split('.');
        for (var i = 1; i < labels.Length - 1; i++)
        {
            var zone = string.Join(".", labels, i, labels.Length - i);
            var response = await AliAsync("DescribeDomainRecords", ("DomainName", ToAscii(zone)), ("PageSize", "1"));
            if (response.Ok)
            {
                var rr = string.Join(".", labels, 0, i);
                _ctx.Log($"云解析托管域判定：{recordName} → 主域 {zone}，主机记录 {rr}");
                return (zone, rr);
            }
            _ctx.Log($"「{zone}」不是当前账号的已托管域名（{response.Code}），继续向上查找…");
        }
        throw new Exception($"阿里云云解析中找不到「{recordName}」所属的托管域名。" +
                                    "请确认该域名已添加到当前 AccessKey 所属账号，且该 AK 有解析读写权限。");
    }

    /// <summary>确保 TXT 记录为目标值：同值复用、同名改值走更新、都没有才新增（云解析禁止重复记录）。</summary>
    private async Task EnsureTxtRecordAsync(string zone, string rr, string value)
    {
        var listed = await RequireAliAsync("DescribeDomainRecords",
            ("DomainName", ToAscii(zone)), ("RRKeyWord", rr), ("TypeKeyWord", "TXT"), ("PageSize", "100"));
        var records = (listed["Domains"]?["Record"] as JArray) ?? new JArray();
        var sameValue = records.FirstOrDefault(record => record["RR"]?.Value<string>() == rr
                                                       && record["Value"]?.Value<string>() == value);
        if (sameValue != null)
        {
            Track(sameValue["RecordId"]?.Value<string>());
            _ctx.Log($"TXT 记录已存在且值一致，复用：{rr}.{zone}");
            return;
        }

        var sameName = records.FirstOrDefault(record => record["RR"]?.Value<string>() == rr);
        if (sameName != null)
        {
            var recordId = sameName["RecordId"]?.Value<string>();
            await RequireAliAsync("UpdateDomainRecord", ("RecordId", recordId), ("RR", rr),
                ("Type", "TXT"), ("Value", value), ("TTL", "600"));
            Track(recordId);
            _ctx.Log($"已把旧 TXT 记录改为本次挑战值（RecordId={recordId}）。");
            return;
        }

        var added = await RequireAliAsync("AddDomainRecord", ("DomainName", ToAscii(zone)), ("RR", rr),
            ("Type", "TXT"), ("Value", value), ("TTL", "600"));
        Track(added["RecordId"]?.Value<string>());
        _ctx.Log($"已新增 TXT 记录（RecordId={added["RecordId"]}）。");
    }

    private void Track(string recordId)
    {
        if (!string.IsNullOrEmpty(recordId) && !_touchedRecordIds.Contains(recordId))
        {
            _touchedRecordIds.Add(recordId);
        }
    }

    private async Task CleanupTxtRecordsAsync(bool cleanupTxt)
    {
        if (_touchedRecordIds.Count == 0)
        {
            return;
        }
        if (!cleanupTxt)
        {
            _ctx.Log($"按 AcmeCleanupTxt=0 保留本次的 {string.Join("、", _touchedRecordIds)} 号 TXT 记录（排障后可手动删除）。");
            return;
        }
        foreach (var recordId in _touchedRecordIds)
        {
            var response = await AliAsync("DeleteDomainRecord", ("RecordId", recordId));
            _ctx.Log(response.Ok ? $"已删除 TXT 记录 {recordId}。" : $"TXT 记录 {recordId} 删除失败（可忽略，下次会复用）：{response.Code} {response.Message}");
        }
    }

    private async Task<JObject> RequireAliAsync(string action, params (string Key, string Value)[] biz)
    {
        var response = await AliAsync(action, biz);
        if (!response.Ok)
        {
            throw new Exception($"阿里云云解析 {action} 失败：{response.Code} {response.Message}");
        }
        return response.Json;
    }

    /// <summary>云解析 RPC 调用：参数按字典序拼接 → HMAC-SHA1 签名 → GET。失败不抛，交调用方判定
    /// （ResolveZoneAsync 要靠「域名未托管」的错误码继续向上找主域）。</summary>
    private async Task<AliResponse> AliAsync(string action, params (string Key, string Value)[] biz)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["AccessKeyId"] = _accessKeyId,
            ["Action"] = action,
            ["Format"] = "JSON",
            ["SignatureMethod"] = "HMAC-SHA1",
            ["SignatureNonce"] = Guid.NewGuid().ToString("N"),
            ["SignatureVersion"] = "1.0",
            ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["Version"] = AliDnsVersion
        };
        foreach (var (key, value) in biz)
        {
            parameters[key] = value ?? string.Empty;
        }

        var canonical = string.Join("&", parameters.Select(item => $"{PercentEncode(item.Key)}={PercentEncode(item.Value)}"));
        var stringToSign = $"GET&%2F&{PercentEncode(canonical)}";
        var signature = Convert.ToBase64String(HMACSHA1.HashData(
            Encoding.UTF8.GetBytes($"{_accessKeySecret}&"), Encoding.UTF8.GetBytes(stringToSign)));
        var url = $"{AliDnsEndpoint}?Signature={PercentEncode(signature)}&{canonical}";

        using var response = await _ctx.Http.GetAsync(url, _ct);
        var text = await response.Content.ReadAsStringAsync(_ct);
        JObject json;
        try
        {
            json = string.IsNullOrWhiteSpace(text) ? new JObject() : JsonConvert.DeserializeObject<JObject>(text);
        }
        catch (JsonException)
        {
            return new AliResponse(false, null, $"Http{(int)response.StatusCode}", Truncate(text));
        }
        var code = json?["Code"]?.Value<string>();
        if (!response.IsSuccessStatusCode || !string.IsNullOrEmpty(code))
        {
            return new AliResponse(false, json, code ?? $"HTTP {(int)response.StatusCode}",
                json?["Message"]?.Value<string>() ?? Truncate(text));
        }
        return new AliResponse(true, json, null, null);
    }

    /// <summary>阿里云要求的 RFC3986 百分号编码（空格 %20、* → %2A、~ 不转义、十六进制大写）。</summary>
    private static string PercentEncode(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in Encoding.UTF8.GetBytes(value ?? string.Empty))
        {
            var ascii = (char)ch;
            var keep = ascii is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~';
            if (keep)
            {
                builder.Append(ascii);
            }
            else
            {
                builder.Append('%').Append(ch.ToString("X2"));
            }
        }
        return builder.ToString();
    }

    // ==================================================================== 容器重启与通知

    private async Task<List<string>> RestartContainersAsync(List<string> containers)
    {
        var results = new List<string>();
        if (containers.Count == 0)
        {
            _ctx.Log("未配置 AcmeContainers，跳过容器重启（需要 nginx 加载新证书时请配置容器名）。");
            return results;
        }
        if (_ctx.Docker == null)
        {
            throw new Exception("当前后端未提供 ctx.Docker 门面，请部署批次 1 的后端版本，或把 AcmeContainers 置为 none 由人工重载。");
        }
        foreach (var container in containers.Where(name => !string.Equals(name, "none", StringComparison.OrdinalIgnoreCase)))
        {
            string line;
            try
            {
                await _ctx.Docker.RestartAsync(container, 10, _ct);
                await Task.Delay(TimeSpan.FromSeconds(3), _ct);
                line = await _ctx.Docker.IsRunningAsync(container, _ct)
                    ? $"容器 {container} 已重启并运行中"
                    : $"容器 {container} 重启指令已发出但未探到运行状态，请人工确认";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // 证书已落盘、台账已写入：重载失败不该让整次执行连带失败并吞掉通知，
                // 记进结果让通知高亮，由人工 reload（否则用户以为证书已经生效了）
                line = $"容器 {container} 重启失败（证书已在盘上，需人工重载）：{e.Message}";
            }
            results.Add(line);
            _ctx.Log(line + "。");
        }
        return results;
    }

    private async Task SendSummaryAsync(List<string> domains, string certDir, DateTime notAfter, List<string> files, List<string> restartResults)
    {
        var left = (notAfter - DateTime.Now).TotalDays;
        var content = $"证书已续签：{string.Join("、", domains)}\n" +
                      $"到期：{notAfter:yyyy-MM-dd HH:mm}（剩余 {left:F0} 天）\n" +
                      $"目录：{certDir}/（下载根内相对路径）\n" +
                      $"文件：{string.Join("、", files)}\n" +
                      $"容器：{(restartResults.Count > 0 ? string.Join("；", restartResults) : "未重启")}";
        _ctx.Log(content);
        if (!_ctx.EnablePush)
        {
            _ctx.Log("EnablePush=false，本次不发送通知。");
            return;
        }
        await _ctx.Notify.SendAsync("ACME 证书续期", content, _ct);
    }

    /// <summary>签发失败也要出声：通知里带阶段、域名、现网证书是否受影响与原因，再交回原始异常
    /// （本方法自身吞掉所有异常，绝不掩盖真正的失败原因）。</summary>
    private async Task NotifyFailureAsync(Exception error)
    {
        var content = $"证书续签失败（阶段：{_stage}）\n" +
                      $"域名：{(_domains.Count > 0 ? string.Join("、", _domains) : "未解析成功（检查 AcmeDomains）")}\n" +
                      $"现网证书：{(_filesWritten ? "新证书已写入盘上，但流程未走完，请确认 nginx 是否已加载新证书" : "未改动，仍是原证书（到期时间未变）")}\n" +
                      $"目录：{_certDir}/（下载根内相对路径）\n" +
                      $"原因：{Truncate(error.Message, 500)}";
        _ctx.Log(content);
        if (!_ctx.EnablePush)
        {
            _ctx.Log("EnablePush=false，失败详情仅记日志不发送通知。");
            return;
        }
        try
        {
            await _ctx.Notify.SendAsync("ACME 证书续期失败", content, _ct);
        }
        catch (Exception e)
        {
            _ctx.Log($"失败通知未能送达（原因见上一条）：{e.Message}");
        }
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static JObject ParseJson(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JObject();
        }
        try
        {
            return JsonConvert.DeserializeObject<JObject>(text);
        }
        catch (JsonException)
        {
            throw new Exception($"{what} 返回了非 JSON 内容：{Truncate(text)}");
        }
    }

    private static string Truncate(string text, int max = 240)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(空响应)";
        }
        return text.Length <= max ? text : text[..max] + "…";
    }
}
