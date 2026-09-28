using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class ProbeSelfTest
{
    internal static int Run()
    {
        try
        {
            Assert(Probe.WeixinUri("ilink/bot/getupdates").Host == "ilinkai.weixin.qq.com");
            foreach (var url in new[] { "http://ilinkai.weixin.qq.com/", "https://evil.com/",
                         "https://weixin.qq.com.evil.com/", "https://ilinkai.weixin.qq.com:444/",
                         "https://user@ilinkai.weixin.qq.com/", "https://ilinkai.weixin.qq.com/path/" })
            {
                var blocked = false;
                try { Probe.WeixinUri("getupdates", new Uri(url)); }
                catch (ProbeException) { blocked = true; }
                Assert(blocked);
            }
            using var reply = JsonDocument.Parse("""{"ret":-14,"errcode":-14,"errmsg":"secret"}""");
            var failed = false;
            try { Probe.CheckBusiness(reply.RootElement); }
            catch (ProbeException e) { failed = !e.Message.Contains("secret"); }
            Assert(failed);
            using var qqAuth = JsonDocument.Parse("""{"access_token":"SAMPLE","expires_in":7200}""");
            var now = DateTimeOffset.UtcNow;
            var parsedToken = QqProbe.ParseToken(qqAuth.RootElement, now);
            Assert(parsedToken.Token == "SAMPLE" && parsedToken.RefreshAt == now.AddSeconds(7140));
            using var qqBadAuth = JsonDocument.Parse("""{"access_token":"SAMPLE","expires_in":0}""");
            var badTokenRejected = false;
            try { QqProbe.ParseToken(qqBadAuth.RootElement, now); }
            catch (ProbeException) { badTokenRejected = true; }
            Assert(badTokenRejected);
            using var qqExt = JsonDocument.Parse("""["msg_idx=REFIDX_abc==","auth_token=DO_NOT_PRINT"]""");
            Assert(QqProbe.GetMessageIndex(qqExt.RootElement) == "REFIDX_abc==");
            using var qqDuplicate = JsonDocument.Parse("""["msg_idx=one","msg_idx=two"]""");
            Assert(QqProbe.GetMessageIndex(qqDuplicate.RootElement) is null);
            using var inbound = JsonDocument.Parse("""{"message_id":987654321,"item_list":[{"type":1,"text_item":{"text":"Q-P0-ABC"}}]}""");
            Assert(Probe.Field(inbound.RootElement, "message_id") is { ValueKind: JsonValueKind.Number });
            Assert(WeixinProbe.HasChallenge(inbound.RootElement, "Q-P0-ABC"));
            Assert(!WeixinProbe.HasChallenge(inbound.RootElement, "Q-P0-OTHER"));
            TestEncryptedRestart();
            TestPinnedNetworkingAsync().GetAwaiter().GetResult();
            Console.WriteLine("离线安全/字段自检通过（不代表平台收发或资格验证）。");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"离线自检失败：{e.GetType().Name}。");
            return 1;
        }
    }

    private static async Task TestPinnedNetworkingAsync()
    {
        foreach (var ip in new[] { "127.0.0.1", "10.0.0.7", "172.16.0.1", "192.168.5.1", "169.254.1.1",
                     "100.64.0.1", "198.18.1.1", "::1", "fc00::1", "fe80::1", "2001:db8::1", "2002:0a00:0001::1" })
            Assert(!ProbeNetwork.IsPublicAddress(IPAddress.Parse(ip)));
        Assert(ProbeNetwork.IsPublicAddress(IPAddress.Parse("1.1.1.1")));
        Assert(ProbeNetwork.IsPublicAddress(IPAddress.Parse("2606:4700:4700::1111")));
        Assert(ProbeNetwork.IsAllowedHost("api.bot.qq.com") && ProbeNetwork.IsAllowedHost("ilinkai.weixin.qq.com"));
        Assert(!ProbeNetwork.IsAllowedHost("weixin.qq.com.attacker.com") && !ProbeNetwork.IsAllowedHost("localhost"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var initial = Volatile.Read(ref ProbeNetwork.ConnectionChecks);
        using (var ws = new ClientWebSocket())
        {
            ws.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            var blocked = false;
            try { await ws.ConnectAsync(new Uri("wss://localhost/"), ProbeNetwork.WebSocketInvoker, cts.Token); }
            catch (Exception e) when (e is ProbeException or WebSocketException or HttpRequestException) { blocked = true; }
            Assert(blocked && Volatile.Read(ref ProbeNetwork.ConnectionChecks) > initial);
        }
        initial = Volatile.Read(ref ProbeNetwork.ConnectionChecks);
        var httpBlocked = false;
        try { await Probe.Http.GetAsync("https://127.0.0.1/", cts.Token); }
        catch (Exception e) when (e is ProbeException or HttpRequestException) { httpBlocked = true; }
        Assert(httpBlocked && Volatile.Read(ref ProbeNetwork.ConnectionChecks) > initial);
    }

    private static void TestEncryptedRestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "quantum-p0-state-selftest-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "weixin.state");
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            using (var store = WeixinStateStorage.ForSelfTest(path, (byte[])key.Clone()))
            {
                Assert(store.Load() is null);
                store.Save(new WeixinProbeState("test-bot", "test-peer", "FAKE_TEST_TOKEN", "https://ilinkai.weixin.qq.com/", "cursor-1", ["10001"]));
                var cannotOpenTwice = false;
                try { using var concurrent = WeixinStateStorage.ForSelfTest(path, (byte[])key.Clone()); }
                catch (IOException) { cannotOpenTwice = true; }
                Assert(cannotOpenTwice);
            }
            Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("FAKE_TEST_TOKEN", StringComparison.Ordinal));
            using (var restored = WeixinStateStorage.ForSelfTest(path, (byte[])key.Clone()))
            {
                var state = restored.Load() ?? throw new ProbeException("重启状态丢失");
                Assert(state.Cursor == "cursor-1" && state.AttemptedMessageIds.SequenceEqual(["10001"]));
                restored.Save(state with { Cursor = "cursor-2", AttemptedMessageIds = [.. state.AttemptedMessageIds, "10002"] });
            }
            var wrongKey = (byte[])key.Clone();
            wrongKey[0] ^= 0x7F;
            using (var store = WeixinStateStorage.ForSelfTest(path, wrongKey))
            {
                var blocked = false;
                try { store.Load(); } catch (ProbeException) { blocked = true; }
                Assert(blocked);
            }
            using (var restored = WeixinStateStorage.ForSelfTest(path, (byte[])key.Clone()))
            {
                var state = restored.Load();
                Assert(state?.Cursor == "cursor-2" && state.AttemptedMessageIds.SequenceEqual(["10001", "10002"]));
            }
            var tampered = File.ReadAllBytes(path);
            tampered[^1] ^= 1;
            File.WriteAllBytes(path, tampered);
            using (var store = WeixinStateStorage.ForSelfTest(path, (byte[])key.Clone()))
            {
                var blocked = false;
                try { store.Load(); } catch (ProbeException) { blocked = true; }
                Assert(blocked);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".lock")) File.Delete(path + ".lock");
            if (Directory.Exists(folder)) Directory.Delete(folder); // 单层已清空的自检目录，绝不递归。
        }
    }

    private static void Assert(bool ok)
    {
        if (!ok) throw new ProbeException("断言失败");
    }
}
