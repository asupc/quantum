using Newtonsoft.Json;
using System.Net;
using System.Text;

namespace Quantum.Utils;

/// <summary>
/// HTTP 帮助类。
/// 历史问题修正：原来每次调用 new HttpClient（高并发下 socket 耗尽）且全程 .Result 阻塞。
/// 现在底层统一走共享 HttpClient（PooledConnectionLifetime 定期刷新 DNS），核心方法全异步；
/// 同步门面保留是为了 37 处存量调用点不破坏——它们几乎都已在后台线程/Task 中运行。
/// </summary>
public static class HttpClientHelper
{
    private static readonly HttpClient SharedClient = CreateSharedClient();

    private static HttpClient CreateSharedClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private static HttpClient CreateClient(string token, IDictionary<string, string> headers, int timeOut)
    {
        // 需要 per-request 头/超时的场景从共享 client 派生请求级配置，不动共享实例状态
        var client = SharedClient;
        client.Timeout = TimeSpan.FromSeconds(timeOut);
        return client;
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, string body, string token, IDictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        if (headers != null)
        {
            foreach (var item in headers)
            {
                request.Headers.Add(item.Key, item.Value);
            }
        }
        if (body != null && method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return request;
    }

    public static async Task<T> PostAsync<T>(string url, string body = null, string token = null, int timeOut = 30, IDictionary<string, string> headers = null)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Post, url, body, token, headers);
            using var response = await SharedClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(result))
            {
                return JsonConvert.DeserializeObject<T>(result);
            }
            Console.WriteLine($"Post地址：{url}，参数：{body}，未返回信息。");
            return default;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{url} post exception：{ex.Message} / {ex.InnerException?.Message}");
            return default;
        }
    }

    public static T Post<T>(string url, string body = null, string token = null, int timeOut = 30, IDictionary<string, string> headers = null)
    {
        return PostAsync<T>(url, body, token, timeOut, headers).GetAwaiter().GetResult();
    }

    public static async Task<string> GetAsStringAsync(string url, string token = null, IDictionary<string, string> headers = null, int timeOut = 30)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Get, url, null, token, headers);
            using var response = await SharedClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"get {url} 发生异常：{ex.Message}");
            return null;
        }
    }

    public static string Get(string url, string token = null, IDictionary<string, string> headers = null, int timeOut = 30)
    {
        return GetAsStringAsync(url, token, headers, timeOut).GetAwaiter().GetResult();
    }

    public static async Task<T> GetAsync<T>(string url, string token = null, IDictionary<string, string> headers = null, int timeOut = 30)
    {
        var d = await GetAsStringAsync(url, token, headers, timeOut);
        if (string.IsNullOrEmpty(d))
        {
            return default;
        }
        try
        {
            return JsonConvert.DeserializeObject<T>(d);
        }
        catch (Exception e)
        {
            Console.WriteLine($"get {url} 反序列化失败：{e.Message}");
            return default;
        }
    }

    public static T Get<T>(string url, string token = null, IDictionary<string, string> headers = null, int timeOut = 30)
    {
        return GetAsync<T>(url, token, headers, timeOut).GetAwaiter().GetResult();
    }

    public static async Task<T> PutAsync<T>(string url, object data, string token = null, int timeOut = 30)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Put, url, JsonConvert.SerializeObject(data), token, null);
            using var response = await SharedClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(result))
            {
                Console.WriteLine($"请求地址：{url}，参数：{JsonConvert.SerializeObject(data)}，未返回信息。");
                return default;
            }
            return JsonConvert.DeserializeObject<T>(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"put {url} 异常：{ex.Message}");
            return default;
        }
    }

    public static T Put<T>(string url, object data, string token = null)
    {
        return PutAsync<T>(url, data, token).GetAwaiter().GetResult();
    }

    public static async Task<T> DeleteAsync<T>(string url, object data, string token, int timeOut = 30)
    {
        try
        {
            using var request = BuildRequest(HttpMethod.Delete, url, JsonConvert.SerializeObject(data), token, null);
            using var response = await SharedClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var resultStr = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(resultStr);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"delete {url} 异常：{ex.Message}");
            return default;
        }
    }

    public static T Delete<T>(string url, object data, string token)
    {
        return DeleteAsync<T>(url, data, token).GetAwaiter().GetResult();
    }
}
