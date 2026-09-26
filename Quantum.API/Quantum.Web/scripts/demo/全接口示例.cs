// ============================================================================
// 全接口示例（测试脚本）：覆盖任务契约的全部接口与内置能力用法。
// 运行方式：任务管理 → 新建任务 → 执行脚本选 demo/全接口示例.cs → 手动执行 → 查看日志。
// 涵盖：ctx.Log / ctx.Variables / ctx.TaskName / ctx.EnableProxy / ctx.EnablePush /
//       ctx.Env（增查改启停删）/ ctx.Notify（文本/图片/视频/音频/可点选项/富文本标记）/
//       ctx.CustomData（Data1-15 查询、新增、按 Id 更新、删除、表头）/
//       ctx.Http（外部请求，可选）/ ctx.File（受控文件落盘：下载不覆盖 + 文本覆盖写，可选）/
//       ctx.Docker（容器重启与探活，可选且会真的重启容器）/
//       Newtonsoft.Json 与 System.Text.Json / 哈希与 AES（BCL）/ HtmlAgilityPack / SQLite(:memory:)/
//       子任务替代方案（原「子任务/多步骤任务链」的顺序拆步、按输入分流、循环定时、跨任务交接四类写法）
// 注意：文件系统/进程/反射等在保存门禁中拦截；日志一律 ctx.Log；长循环检查 ct。
// ============================================================================
using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Quantum.Plugins;

public class AllFeaturesDemoTask : IQuantumTask
{
    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // ------------------------------------------------------------------ 1. 基础上下文
        ctx.Log("=== 1. 基础上下文 ===");
        ctx.Log($"TaskName    = {ctx.TaskName}");
        ctx.Log($"EnableProxy = {ctx.EnableProxy}（外部请求走代理开关）");
        ctx.Log($"EnablePush  = {ctx.EnablePush}（推送开关，发送通知前可自判）");
        // Variables：平台启用中的环境变量 + 任务/外触注入的变量（同名以 & 连接，此处取单值）
        ctx.Log($"Variables 共 {ctx.Variables.Count} 项");
        if (ctx.Variables.TryGetValue("IsSystem", out var isSystem))
        {
            ctx.Log($"Variables[\"IsSystem\"] = {isSystem}（定时触发注入；手动/指令/外触各有自己的标记变量）");
        }

        // ------------------------------------------------------------------ 2. 环境变量门面（进程内直调，免 HTTP/免令牌）
        ctx.Log("=== 2. ctx.Env 环境变量门面 ===");
        var demoName = "DemoAllFeatures" + DateTime.Now.ToString("yyyyMMdd");
        var key = "demo_" + DateTime.Now.Second;
        try
        {
            // 2.1 保存（存在即更新、不存在即新增；名称规则：字母开头+字母/数字/下划线，≤64）
            await ctx.Env.SaveAsync(demoName, $"{key}=v1", "全接口示例自动创建", enabled: true, ct);
            ctx.Log($"SaveAsync 新增：{demoName}");

            // 2.2 查询（name 精确匹配；key 为值包含匹配；两者都空返回全部）
            var all = await ctx.Env.QueryAsync(name: null, key: null, ct: ct);
            ctx.Log($"QueryAsync 全量：共 {all.Count} 条");
            var mine = await ctx.Env.QueryAsync(name: demoName, key: key, ct: ct);
            foreach (var env in mine)
            {
                ctx.Log($"QueryAsync 命中：{env.Name} = {env.Value}（Remark={env.Remark}, Enabled={env.Enabled}, UpdateTime={env.UpdateTime:yyyy-MM-dd HH:mm:ss}）");
            }

            // 2.3 停用/启用
            await ctx.Env.SetEnabledAsync(demoName, enabled: false, ct);
            ctx.Log($"SetEnabledAsync(false) 后 Enabled = {(await ctx.Env.QueryAsync(demoName, null, ct)).Single().Enabled}");
            await ctx.Env.SetEnabledAsync(demoName, enabled: true, ct);
            ctx.Log($"SetEnabledAsync(true)  后 Enabled = {(await ctx.Env.QueryAsync(demoName, null, ct)).Single().Enabled}");

            // 2.4 按名删除（示例自清理；后续任务要复用变量请去掉本段）
            await ctx.Env.DeleteByNameAsync(demoName, ct);
            ctx.Log($"DeleteByNameAsync 后剩余同名 { (await ctx.Env.QueryAsync(demoName, null, ct)).Count } 条");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // 示例脚本对环境问题保持鲁棒：如数据库架构遗留（旧列无默认值）导致写入失败，说明后继续后续章节
            ctx.Log($"环境变量门面调用失败，跳过本节：{e.Message}");
        }

        // ------------------------------------------------------------------ 2.5 自定义数据门面（进程内直调，原 addCustomData/getCustomData 等价）
        ctx.Log("=== 2.5 ctx.CustomData 自定义数据门面 ===");
        var demoType = "DemoAllFeatures";
        try
        {
            await ctx.CustomData.AddAsync(
            [
                new QuantumCustomDataValue
                {
                    Type = demoType,
                    Data1 = "示例条目",
                    Data2 = $"value-{DateTime.Now:HHmmss}",
                    Data3 = "全接口示例自动创建",
                    Data4 = ctx.Variables.TryGetValue("CommunicationUserId", out var demoUid) ? demoUid : null
                }
            ], ct);
            ctx.Log($"AddAsync 新增 1 条（Type={demoType}）");

            var found = await ctx.CustomData.QueryAsync(demoType, data1: "示例条目", ct: ct);
            ctx.Log($"QueryAsync 命中 {found.Count} 条，首条 Data2={found[0].Data2}（CreateTime={found[0].CreateTime:HH:mm:ss}）");

            // 2.6 全列过滤（Data6-Data15 + 创建时间范围）与更新、表头：健康/项目类脚本的常用形态
            await ctx.CustomData.AddAsync(
            [
                new QuantumCustomDataValue
                {
                    Type = demoType,
                    Data1 = "示例条目",
                    Data11 = DateTime.Now.ToString("yyyy-MM-dd HH"),
                    Data12 = "detail-1"
                }
            ], ct);
            var byAdvancedColumn = await ctx.CustomData.QueryAsync(demoType, new QuantumCustomDataFilter
            {
                Data11 = DateTime.Now.ToString("yyyy-MM-dd"),
                CreateTimeStart = DateTime.Now.AddDays(-1),
                CreateTimeEnd = DateTime.Now.AddDays(1)
            }, ct);
            ctx.Log($"按 Data11/创建时间过滤命中 {byAdvancedColumn.Count} 条，首条 Data12={byAdvancedColumn.FirstOrDefault()?.Data12}");
            if (byAdvancedColumn.Count > 0)
            {
                // 按 Id 更新（须带查询结果里的 Id，否则会被拒绝）
                await ctx.CustomData.UpdateAsync(
                    [byAdvancedColumn[0] with { Data12 = "detail-updated" }], ct);
                var updated = await ctx.CustomData.QueryAsync(demoType, new QuantumCustomDataFilter { Data12 = "detail-updated" }, ct);
                ctx.Log($"UpdateAsync 后 Data12=detail-updated 命中 {updated.Count} 条");
            }
            // 表头（数据管理页列名，titles 依次对应 Data1-Data15）
            await ctx.CustomData.SaveTitleAsync(demoType, "全接口示例数据", ["列一", "列二", "列三"], ct);
            ctx.Log("SaveTitleAsync 写入表头：全接口示例数据");

            // 示例自清理（按查询结果的 Id 删除）
            await ctx.CustomData.DeleteAsync(found.Select(x => x.Id).ToArray(), ct);
            await ctx.CustomData.DeleteAsync((await ctx.CustomData.QueryAsync(demoType, ct: ct)).Select(x => x.Id).ToArray(), ct);
            ctx.Log($"DeleteAsync 后剩余 { (await ctx.CustomData.QueryAsync(demoType, ct: ct)).Count } 条");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            ctx.Log($"自定义数据门面调用失败，跳过本节：{e.Message}");
        }

        // ------------------------------------------------------------------ 3. JSON（两套库都在引用集内）
        ctx.Log("=== 3. JSON ===");
        var jobj = new JObject { ["name"] = "quantum", ["version"] = 10 };
        ctx.Log($"Newtonsoft 序列化：{jobj.ToString(Formatting.None)}");
        var stj = System.Text.Json.JsonSerializer.Serialize(new { name = "quantum", version = 10 });
        ctx.Log($"System.Text.Json：{stj}");
        var parsed = JsonConvert.DeserializeObject<JObject>("{\"ok\":true}");
        ctx.Log($"Newtonsoft 反序列化 ok = {parsed["ok"]}");

        // ------------------------------------------------------------------ 4. 哈希与 AES（BCL，覆盖 crypto-js 用途）
        ctx.Log("=== 4. 哈希与 AES ===");
        var bytes = Encoding.UTF8.GetBytes("quantum-demo");
        ctx.Log($"MD5    = {Convert.ToHexString(MD5.HashData(bytes))}");
        ctx.Log($"SHA256 = {Convert.ToHexString(SHA256.HashData(bytes))}");
        using (var aes = Aes.Create())
        {
            aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes("demo-key"));
            aes.IV = new byte[16];
            using var encryptor = aes.CreateEncryptor();
            var cipher = encryptor.TransformFinalBlock(bytes, 0, bytes.Length);
            using var decryptor = aes.CreateDecryptor();
            var plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            ctx.Log($"AES 加解密回读：{Encoding.UTF8.GetString(plain)}（{cipher.Length} 字节密文）");
        }

        // ------------------------------------------------------------------ 5. HTML 解析（HtmlAgilityPack，jsdom 替代）
        ctx.Log("=== 5. HtmlAgilityPack ===");
        var doc = new HtmlDocument();
        doc.LoadHtml("<html><body><ul><li>签到</li><li>查询</li></ul></body></html>");
        var items = doc.DocumentNode.SelectNodes("//li").Select(n => n.InnerText).ToList();
        ctx.Log($"HTML 解析 li 节点：{string.Join("、", items)}");

        // ------------------------------------------------------------------ 6. SQLite（:memory:，无需文件系统）
        ctx.Log("=== 6. SQLite(:memory:) ===");
        using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE t(id INTEGER, name TEXT); INSERT INTO t VALUES(1,'量子'),(2,'助手');";
                await cmd.ExecuteNonQueryAsync(ct);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, name FROM t ORDER BY id";
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    ctx.Log($"SQLite 行：id={reader.GetInt64(0)}, name={reader.GetString(1)}");
                }
            }
        }

        // ------------------------------------------------------------------ 7. 外部请求（ctx.Http：预配代理/超时 100s；仅第三方站点）
        ctx.Log("=== 7. ctx.Http 外部请求 ===");
        // 可在任务环境变量里配置 DemoHttpUrl 控制是否实测；默认跳过以避免外网依赖
        if (ctx.Variables.TryGetValue("DemoHttpUrl", out var url) && !string.IsNullOrEmpty(url))
        {
            try
            {
                using var response = await ctx.Http.GetAsync(url, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                ctx.Log($"GET {url} → HTTP {(int)response.StatusCode}，{body.Length} 字符");
            }
            catch (Exception e)
            {
                ctx.Log($"外部请求异常（不影响后续步骤）：{e.Message}");
            }
        }
        else
        {
            ctx.Log("未配置 DemoHttpUrl 变量，跳过外部请求实测。");
        }

        // ------------------------------------------------------------------ 8. ctx.File 受控文件落盘（产物落盘唯一通道；System.IO 门禁仍禁）
        ctx.Log("=== 8. ctx.File 受控文件落盘 ===");
        // 可写范围限定在下载根目录（appsettings.json Quantum:FileDownloadRoot，缺省 ./downloads）：
        // 子目录只允许根内相对路径且防穿越、文件名自动清洗、重名自动追加「 (n)」不覆盖；
        // 复用任务 HttpClient（代理/超时与第 7 节一致）。与第 7 节一致，配变量才实测。
        if (ctx.Variables.TryGetValue("DemoFileUrl", out var fileUrl) && !string.IsNullOrEmpty(fileUrl))
        {
            try
            {
                var saved = await ctx.File.DownloadAsync(fileUrl, "全接口示例下载.bin", subDir: "demo", ct);
                ctx.Log($"DownloadAsync 落盘：{saved.FullPath}（{saved.Length} 字节，RelativePath={saved.RelativePath}）");
                // 落盘产物回推 App 媒体气泡（可选）：服务端产物用 AppMedia 相对地址拼法（无外链时效，
                // App 按当前登录服务器地址补全并带鉴权播放），实战见 scripts/media_saver.cs：
                //   await ctx.Notify.SendAudioAsync("api/AppMedia/file?path=" + Uri.EscapeDataString(saved.RelativePath), "落盘音频回推", ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                ctx.Log($"文件落盘失败（不影响后续步骤）：{e.Message}");
            }
        }
        else
        {
            ctx.Log("未配置 DemoFileUrl 变量，跳过文件落盘实测。");
        }

        // ------------------------------------------------------------------ 8.5 ctx.File 文本覆盖落盘（证书这类需原地更新的产物）
        // DownloadAsync 的「重名自动追加序号不覆盖」对证书/配置类产物不适用：SaveTextAsync 同名直接覆盖，
        // 先写同目录临时文件再原子替换（读侧不会看到半截内容），内容上限 1 MiB，目录与文件名约束同上。
        if (ctx.Variables.TryGetValue("DemoSaveText", out var textBody) && !string.IsNullOrEmpty(textBody))
        {
            try
            {
                var written = await ctx.File.SaveTextAsync(textBody, "全接口示例.txt", "demo", ct);
                ctx.Log($"SaveTextAsync 落盘：{written.RelativePath}（{written.Length} 字节）→ {written.FullPath}；再跑一次原地覆盖");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                ctx.Log($"文本落盘失败（不影响后续步骤）：{e.Message}");
            }
        }
        else
        {
            ctx.Log("未配置 DemoSaveText 变量，跳过文本覆盖落盘实测。");
        }

        // ------------------------------------------------------------------ 8.6 ctx.Docker 容器运维（只放开「重启 + 探活」）
        // 供「产物落盘后要让服务重载」的脚本使用（实战：scripts/acme_cert.cs 续签证书后重启 nginx）。
        // 停止/删除/exec/镜像/网络/卷一律不放开；容器名取自环境变量，勿硬编码，结果应回显到通知。
        if (!ctx.Variables.TryGetValue("DemoDockerContainer", out var container) || string.IsNullOrEmpty(container))
        {
            ctx.Log("未配置 DemoDockerContainer 变量，跳过容器重启实测（填了会真的重启该容器，慎填）。");
        }
        else if (ctx.Docker == null)
        {
            ctx.Log("当前后端未提供 ctx.Docker 门面，跳过。");
        }
        else
        {
            try
            {
                ctx.Log($"探活 {container}：运行中 = {await ctx.Docker.IsRunningAsync(container, ct)}");
                await ctx.Docker.RestartAsync(container, waitBeforeKillSeconds: 10, ct: ct);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                ctx.Log($"重启指令已发出，复核 {container}：运行中 = {await ctx.Docker.IsRunningAsync(container, ct)}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                ctx.Log($"容器重启失败（不影响后续步骤）：{e.Message}");
            }
        }

        // ------------------------------------------------------------------ 9. 通知门面（落 App 会话；是否发送建议自判 EnablePush）
        ctx.Log("=== 9. ctx.Notify 通知门面 ===");
        if (ctx.EnablePush)
        {
            await ctx.Notify.SendAsync("全接口示例任务", $"「{ctx.TaskName}」全接口示例执行完成：{DateTime.Now:yyyy-MM-dd HH:mm:ss}", ct);
            // 富媒体发送说明（图片/视频）：
            //   1) 图片+文字是【同一条消息】：SendImageAsync(地址, 配文) 的配文 caption 与图片合成
            //      一个气泡——App 端媒体在上、文字在下（类似微信图片配字），不是「一条图片+一条文字」
            //      两条消息；需要图文并茂时务必走 caption 参数，不要再单独 SendAsync 补一条文字。
            //   2) 视频同理：SendVideoAsync(地址, 配文[, 封面地址])，App 内点按气泡端内播放，
            //      第三参 posterUrl 可选（封面作播放预览）。
            //   3) 第二参 caption 均可空（null/省略 = 纯媒体无配文）；取消令牌请用命名实参
            //      ct: 传递——位置传参第三位现在是 options/封面地址。
            //   4) 音频：SendAudioAsync(地址, 配文)，App 会话里是可点播的音频气泡。
            //   5) 媒体地址两可：外链 http/https（App 原样加载，如站点封面图）；
            //      平台 AppUpload 的 FileId（App 端走鉴权下载）。
            await ctx.Notify.SendImageAsync("https://www.quantum.app/demo.png", "图片配文示例：图文同一条消息、同气泡展示",
                options: [new QuantumOption("ok", "点选回复：好看", Color: "green")], ct: ct);
            await ctx.Notify.SendVideoAsync("https://www.quantum.app/demo.mp4", "视频配文示例：封面作预览、点按端内播放",
                posterUrl: "https://www.quantum.app/demo.jpg", ct: ct);
            await ctx.Notify.SendAudioAsync("https://www.quantum.app/demo.mp3", "音频配文示例：点按播放", ct);
            // 富交互（2026-09-18）：消息可携带可点选项，App 点按即以 reply（缺省 key）回复触发本任务；
            // 图片消息也可带选项（封面卡片点选）。正文富文本标记见 QuantumText（多彩 tag）。
            await ctx.Notify.SendOptionsAsync("示例：点选一项继续", new[]
            {
                new QuantumOption("1", "选项一：直接回复 1"),
                new QuantumOption("save", "选项二：回复「保存3」", Reply: "保存3", Color: "blue")
            }, ct);
            // 富文本标记（QuantumText）：彩色文字 / 胶囊标签，App 渲染为着色文字与圆角标签，
            // 颜色仅 red/green/orange/blue/purple/gray（非法颜色按原文字面显示，不影响送达）。
            await ctx.Notify.SendAsync("全接口示例任务",
                $"富文本示例：{QuantumText.Tag("green", "成功")} 胶囊标签、{QuantumText.Color("red", "彩色强调")}、"
                + $"{QuantumText.Color("blue", "蓝色文字")}；裸链接 http://www.quantum.app 由 App 自动识别为可点链接。", ct);
            ctx.Log("通知/图片/视频/音频/可点选项/富文本标记已发送。");
        }
        else
        {
            ctx.Log("EnablePush=false，按开关跳过通知发送。");
        }

        // ------------------------------------------------------------------ 10. 协作取消（长耗时/长循环的标准写法）
        ctx.Log("=== 10. 协作取消示例 ===");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!ct.IsCancellationRequested && sw.Elapsed < TimeSpan.FromSeconds(1))
        {
            await Task.Delay(200, ct); // 延迟携带 ct：ForceEndTime 到期立即退出
        }
        ctx.Log($"协作取消演示结束（耗时 {sw.ElapsedMilliseconds}ms，未到期正常走完）。");

        // ------------------------------------------------------------------ 11. 子任务替代方案（原「子任务/多步骤任务链/任务循环」的等价写法）
        // 详见 docs/子任务与任务循环替代方案.md。核心结论：一个任务 = 一段 .cs 源码，
        // 触发只剩「触发指令（可正则）」与「定时执行（Cron）」两种，旧子任务场景按形态收敛为四类：
        //
        // 【顺序多步骤】（原：子任务1→2→3 按序串联）→ 直接在本脚本里顺序写，不再拆步骤：
        //     var html = await ctx.Http.GetStringAsync("https://example.com/login", ct);  // 原子任务1：登录
        //     await ctx.Http.PostAsync(".../checkin", new FormUrlEncodedContent(...), ct); // 原子任务2：复用上一步变量
        //     await ctx.Notify.SendAsync("每日签到", "签到完成", ct);                       // 原子任务3：通知
        //   好处：类型安全、可 try/catch、可共享局部变量。
        //
        // 【按输入分支】（原：指令A触发子任务1、指令B触发子任务2 的对话式分流）→ 单触发指令 + 脚本内 switch：
        //   任务编辑里「触发指令」配宽（如正则 ^(登录|签到|查询)），「指令环境变量名」填 message，
        //   用户在 App 发送的整条消息即注入 ctx.Variables["message"]，脚本内自行分流（见下方演示；
        //   实战范例见 scripts/dygangs_search.cs：搜索关键字与订阅回复按消息形态自动分流）。
        //
        // 【循环 / 定时】（原：任务循环 + WaitTime 等待）→ 二选一或组合：
        //   - 周期重跑：任务「定时执行」填 Cron（如 0 0/30 * * * ? 每 30 分钟），脚本每次执行一轮；
        //   - 单次执行内自循环：while (!ct.IsCancellationRequested) + Task.Delay 控制节奏
        //     （写法见第 10 节；ForceEndTime 到期经 ct 协作取消，长循环务必每次迭代检查 ct）。
        //
        // 【跨任务数据交接】（原：上一步结果留给下一步）→ ctx.CustomData / ctx.Env.SaveAsync 落库传递
        //   （用法见第 2 / 2.5 节）；两个任务各自独立调度，不必再靠子任务链绑定。
        ctx.Log("=== 11. 子任务替代方案：按输入分支演示 ===");
        var message = ctx.Variables.TryGetValue("message", out var msg) ? msg : "";
        if (string.IsNullOrEmpty(message))
        {
            ctx.Log("未带指令消息（手动运行）。实际用法：任务「触发指令」配 ^(登录|签到|查询)，" +
                    "「指令环境变量名」填 message，App 发「签到」后此处即取到整条消息。");
        }
        else
        {
            // 动作词 = 首个空白（半角/全角/制表）之前的部分，其余作参数，等价旧「指令触发子任务」的分流
            var sepIndex = message.IndexOfAny([' ', '　', '\t']);
            var action = sepIndex > 0 ? message[..sepIndex] : message;
            switch (action)
            {
                case "登录":
                    ctx.Log($"分流 → 登录步骤（原文：{message}）");
                    break;
                case "签到":
                    ctx.Log($"分流 → 签到步骤（原文：{message}）");
                    break;
                default:
                    ctx.Log($"分流 → 未知指令「{action}」，可回提示（原文：{message}）");
                    break;
            }
        }

        ctx.Log("=== 全接口示例执行完毕 ===");
    }
}
