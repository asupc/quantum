using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

internal static class Probe
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new ProbeException($"请在本机私密环境变量配置 {name}（勿写入仓库或聊天）。");

    internal static string? Optional(string name) => Environment.GetEnvironmentVariable(name);

    internal static readonly HttpClient Http = new(ProbeNetwork.Handler, disposeHandler: false)
    {
        Timeout = TimeSpan.FromSeconds(65), MaxResponseContentBufferSize = 512 * 1024
    };

    internal static async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                throw new ProbeException("平台响应跳转，探针不会跟随重定向。");
            if (!response.IsSuccessStatusCode)
                throw new ProbeException($"平台 HTTP {(int)response.StatusCode}，未输出响应正文（可能包含敏感数据）。");
            await response.Content.LoadIntoBufferAsync(512 * 1024, ct);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            return json.RootElement.Clone();
        }
    }

    internal static HttpRequestMessage Post(Uri uri, object body)
        => new(HttpMethod.Post, uri) { Content = JsonContent.Create(body, options: Json) };

    internal static string? Text(JsonElement node, string name)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    internal static int? Int(JsonElement node, string name)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result : null;

    internal static JsonElement? Field(JsonElement node, string name)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : null;

    internal static Uri QqUri(string path) => new("https://api.bot.qq.com" + path);

    internal static Uri WeixinUri(string path, Uri? baseUrl = null)
    {
        var root = baseUrl ?? new Uri("https://ilinkai.weixin.qq.com/");
        if (root.Scheme != Uri.UriSchemeHttps || root.Port != 443 || !string.IsNullOrEmpty(root.UserInfo) ||
            !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment) || root.AbsolutePath != "/" ||
            !(root.Host.Equals("weixin.qq.com", StringComparison.OrdinalIgnoreCase) ||
              root.Host.EndsWith(".weixin.qq.com", StringComparison.OrdinalIgnoreCase)))
            throw new ProbeException("微信返回不可信 API 地址；探针已拒绝连接。");
        return new Uri(root, path.TrimStart('/'));
    }

    internal static void CheckBusiness(JsonElement result)
    {
        var ret = Int(result, "ret");
        var code = Int(result, "errcode");
        if (ret is not null and not 0 || code is not null and not 0)
            throw new ProbeException($"平台业务失败：ret={ret?.ToString() ?? "?"} errcode={code?.ToString() ?? "?"}（未输出正文）。");
    }
}

internal sealed class ProbeException(string message) : Exception(message);
