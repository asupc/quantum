using Microsoft.Data.Sqlite;
using Quantum.Application;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;

namespace Quantum.API.Tests;

/// <summary>
/// 门禁对抗性回归（2026-10-10）：活体攻击证明任务脚本可写任意文件、可读写平台库且执行报告成功——
/// 本类为「按证据逐条关闭」的对抗用例，覆盖 8 类逃逸面 + 正例对照（防过度拦截）。
/// 与 ScriptEngineTests 同 Collection：ScriptBuildService 静态编译缓存/门禁规则全局共享，须串行。
/// 断言口径：每个逃逸面取最弱正确结果——Build(...) 返回 Success == false 且 Blocked 含「具名该规则」的条目。
/// </summary>
[Collection("ConstsState")]
public class ScriptGateEscapeTests
{
    // ==================================================================== 逃逸 1：Microsoft.Data.Sqlite 文件库（活体证据）

    [Theory]
    [InlineData("相对路径", "Data Source=../adv.db")]
    [InlineData("绝对路径", "Data Source=C:/quantum-escape/adv.db")]
    [InlineData("平台库（读）", "Data Source=../app.db")]
    [InlineData("平台库（篡改）", "Data Source=../data/quantum.db")]
    public void Escape1_SqliteFileBackedConnectionString_BlockedByNamedRule(string _, string connectionString)
    {
        // 活体证据：new SqliteConnection(文件串) + Open + CREATE TABLE/INSERT 曾被接受（Success:true, Blocked:[]），
        // 并在 scripts/quantum 之外落盘、可读写平台库（t_env/t_log 等）。此处断言连接串参数级规则命中。
        var result = Build("using Microsoft.Data.Sqlite;", $$"""
            using (var conn = new SqliteConnection("{{connectionString}}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE adv(x INTEGER); INSERT INTO adv VALUES(1);";
                cmd.ExecuteNonQuery();
            }
            """);
        AssertBlockedWithCode(result, "arg:sqlite-memory-only");
    }

    [Fact]
    public void Escape1_SqliteNonLiteralConnectionString_BlockedByNamedRule()
    {
        // 编译期看不到值（变量/动态拼接）一律拒绝：只放行含 ":memory:" 的字符串字面量
        var result = Build("using Microsoft.Data.Sqlite;", """
            var connectionString = "Data Source=../adv.db";
            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            """);
        AssertBlockedWithCode(result, "arg:sqlite-memory-only");
    }

    [Fact]
    public void Escape1_SqliteParameterlessCtor_BlockedByNamedRule()
    {
        // 无参构造（DataSource 空）在 SQLite 下等价临时文件库：无 ":memory:" 字面量即拦截
        var result = Build("using Microsoft.Data.Sqlite;", """
            using var conn = new SqliteConnection();
            conn.ConnectionString = "Data Source=../adv.db";
            conn.Open();
            """);
        AssertBlockedWithCode(result, "arg:sqlite-memory-only");
    }

    [Fact]
    public void Escape1_SqliteConnectionStringSetter_BlockedByNamedRule()
    {
        // 先以内存串构造、再改写到文件库：ConnectionString 赋值同样只放行 ":memory:" 字面量
        var result = Build("using Microsoft.Data.Sqlite;", """
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.ConnectionString = "Data Source=../adv.db";
            conn.Open();
            """);
        AssertBlockedWithCode(result, "arg:connection-string-memory-only");
    }

    [Fact]
    public void Escape1_SqliteBaseCtorInitializer_BlockedByNamedRule()
    {
        // 派生类在基类构造初始化器里塞文件串：与直接 new 同一条规则
        var result = Build("using Microsoft.Data.Sqlite;", """
            var conn = new SneakyConnection();
            conn.Open();
            """, extraTypes: """
            public class SneakyConnection : SqliteConnection
            {
                public SneakyConnection() : base("Data Source=../adv.db") { }
            }
            """);
        AssertBlockedWithCode(result, "arg:sqlite-memory-only");
    }

    [Fact]
    public void Escape1_SqliteFactory_BlockedByNamedRule()
    {
        // SqliteFactory.Instance.CreateConnection() 可动态建连接绕开构造实参检查
        var result = Build("using Microsoft.Data.Sqlite;", """
            var conn = SqliteFactory.Instance.CreateConnection();
            conn.ConnectionString = "Data Source=../adv.db";
            conn.Open();
            """);
        Assert.False(result.Success);
        Assert.True(
            result.Blocked.Any(b => b.Code == "type:Microsoft.Data.Sqlite.SqliteFactory"),
            "应命中 SqliteFactory 类型规则，实际：" + Describe(result));
    }

    [Fact]
    public void Escape1_DbConnectionBaseTypedSetter_BlockedByNamedRule()
    {
        // 上转型为 DbConnection 后写 ConnectionString，同样换接文件库（活体验证可落盘）：
        // 成员规则须覆盖 System.Data.Common.DbConnection 声明面，而不只是 SqliteConnection 自身
        var result = Build("using Microsoft.Data.Sqlite;", """
            using var conn = new SqliteConnection("Data Source=:memory:");
            System.Data.Common.DbConnection baseRef = conn;
            baseRef.ConnectionString = "Data Source=../adv.db";
            baseRef.Open();
            """);
        AssertBlockedWithCode(result, "arg:connection-string-memory-only");
    }

    [Fact]
    public void Escape1_TypedescriptorSetter_BlockedByNamedRule()
    {
        // TypeDescriptor 按名写属性（无任何 Type/Delegate 被拦符号）同样能换接文件库（活体验证可落盘）
        var result = Build(null, """
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            var props = System.ComponentModel.TypeDescriptor.GetProperties(conn);
            props["ConnectionString"].SetValue(conn, "Data Source=../adv.db");
            conn.Open();
            """);
        AssertBlockedWithCode(result, "ns:System.ComponentModel");
    }

    [Fact]
    public void Escape1_JsonPopulateObject_BlockedByNamedRule()
    {
        // JsonConvert.PopulateObject 按名灌 ConnectionString：既有的 Newtonsoft 引用面成了属性写入通道
        var result = Build("using Microsoft.Data.Sqlite;", """
            using var conn = new SqliteConnection("Data Source=:memory:");
            Newtonsoft.Json.JsonConvert.PopulateObject("{\"ConnectionString\":\"Data Source=../adv.db\"}", conn);
            conn.Open();
            """);
        AssertBlockedWithCode(result, "arg:json-deserialize-dbconn");
    }

    [Theory]
    [InlineData("Newtonsoft DeserializeObject(json, 运行期 Type)", "Newtonsoft.Json.JsonConvert.DeserializeObject(\"{}\", t)")]
    [InlineData("System.Text.Json Deserialize(json, 运行期 Type)", "System.Text.Json.JsonSerializer.Deserialize(\"{}\", t)")]
    public void Escape1_JsonDeserializeWithRuntimeType_BlockedByNamedRule(string _, string call)
    {
        // 活体探针确认：DeserializeObject(json, x.GetType()) 能按运行期 Type 灌 ConnectionString 并落盘——
        // 静态看不到目标类型，故要求类型实参必须是编译期可见形态（typeof 字面量/泛型实参）
        var result = Build("using Microsoft.Data.Sqlite;", $$"""
            using var probe = new SqliteConnection("Data Source=:memory:");
            var t = probe.GetType();
            var conn = {{call}};
            ctx.Log("" + (conn != null));
            """);
        AssertBlockedWithCode(result, "arg:json-deserialize-dbconn");
    }

    [Theory]
    [InlineData("LoadExtension", "conn.EnableExtensions(true); conn.LoadExtension(\"C:/evil.dll\", \"entry\");")]
    [InlineData("EnableExtensions", "conn.EnableExtensions(true);")]
    [InlineData("BackupDatabase", "using var dest = new SqliteConnection(\"Data Source=../adv.db\"); conn.BackupDatabase(dest);")]
    public void Escape1_SqliteNativeEscapeAndFileBackup_BlockedByNamedRule(string member, string call)
    {
        var result = Build("using Microsoft.Data.Sqlite;", $$"""
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            {{call}}
            ctx.Log("" + conn.State);
            """);
        Assert.False(result.Success, $"应被门禁拦截（{member}），实际：" + Describe(result));
        Assert.True(
            result.Blocked.Any(b => b.Code.StartsWith("member:Microsoft.Data.Sqlite.SqliteConnection.", StringComparison.Ordinal)),
            $"期望命中 SqliteConnection 成员规则，实际：" + Describe(result));
    }

    [Theory]
    [InlineData("DeserializeObject 泛型实参", "Newtonsoft.Json.JsonConvert.DeserializeObject<Microsoft.Data.Sqlite.SqliteConnection>(\"{}\")")]
    [InlineData("ToObject 泛型实参", "Newtonsoft.Json.Linq.JObject.Parse(\"{}\").ToObject<Microsoft.Data.Sqlite.SqliteConnection>()")]
    [InlineData("System.Text.Json typeof 实参", "System.Text.Json.JsonSerializer.Deserialize(\"{}\", typeof(Microsoft.Data.Sqlite.SqliteConnection))")]
    public void Escape1_JsonDeserializeToConnectionType_BlockedByNamedRule(string _, string call)
    {
        var result = Build(null, $"var conn = {call}; ctx.Log(\"\" + (conn != null));");
        AssertBlockedWithCode(result, "arg:json-deserialize-dbconn");
    }

    [Theory]
    [InlineData("ATTACH DATABASE 字面量", "cmd.CommandText = \"ATTACH DATABASE '../adv.db' AS adv\";")]
    [InlineData("VACUUM INTO 字面量", "cmd.CommandText = \"VACUUM INTO '../dumped.db'\";")]
    [InlineData("变量 SQL 文本", "cmd.CommandText = sql;")]
    [InlineData("插值 SQL 文本", "cmd.CommandText = $\"ATTACH DATABASE '{path}' AS adv\";")]
    [InlineData("构造器直传 ATTACH", "cmd = new SqliteCommand(\"ATTACH DATABASE '../adv.db' AS adv\", conn);")]
    [InlineData("构造器直传变量 SQL", "cmd = new SqliteCommand(sql, conn);")]
    public void Escape1_SqliteAttachOrVacuumInto_BlockedByNamedRule(string _, string assignment)
    {
        // 内存库形态并不能挡住 SQL 文本级写文件：ATTACH/VACUUM INTO 可在连接串合规时照样落盘；
        // 非字面量 SQL（变量/插值）编译期看不到内容，一律拒绝；命令文本经构造器直传同样在列
        var result = Build("using Microsoft.Data.Sqlite;", $$"""
            var sql = "SELECT 1";
            var path = "../adv.db";
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using var cmd = conn.CreateCommand();
            {{assignment}}
            ctx.Log("" + cmd.CommandText);
            """);
        AssertBlockedWithCode(result, "sql:command-text-literal");
    }

    [Fact]
    public void Escape1_BlockedLine_MapsToScriptOwnLine()
    {
        // 参数级拦截同样要报脚本自身行号（编译服务前置两行 global using + #line 1，须取映射后行号）
        var source = "using Microsoft.Data.Sqlite;\n"
            + "using Quantum.Plugins;\n"
            + "public class LineProbeTask : IQuantumTask\n"
            + "{\n"
            + "    public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)\n"
            + "    {\n"
            + "        using var conn = new SqliteConnection(\"Data Source=../adv.db\");\n"
            + "        return Task.CompletedTask;\n"
            + "    }\n"
            + "}\n";
        var result = ScriptBuildService.Build(source);
        var blocked = Assert.Single(result.Blocked, b => b.Code == "arg:sqlite-memory-only");
        Assert.Equal(7, blocked.Line);
    }

    // ==================================================================== 逃逸 2：EF Core 原始 SQL / 连接句柄

    [Fact]
    public void Escape2_UseSqliteFileDatabase_BlockedByNamedRule()
    {
        // 活体证据：Microsoft.EntityFrameworkCore* 在编译引用白名单内 → Database.ExecuteSqlRaw / GetDbConnection 可达
        var result = BuildEf("""
            var options = new DbContextOptionsBuilder().UseSqlite("Data Source=../adv.db").Options;
            using var db = new EscapeContext(options);
            ctx.Log("" + db.Database.ProviderName);
            """);
        AssertBlockedWithCode(result, "arg:use-sqlite-memory-only");
    }

    [Theory]
    [InlineData("ExecuteSqlRaw", "db.Database.ExecuteSqlRaw(\"UPDATE t_log SET Remark='tampered'\")")]
    [InlineData("ExecuteSqlRawAsync", "await db.Database.ExecuteSqlRawAsync(\"UPDATE t_log SET Remark='tampered'\", ct)")]
    [InlineData("ExecuteSqlInterpolated", "db.Database.ExecuteSqlInterpolated($\"DELETE FROM t_log\")")]
    [InlineData("ExecuteSqlInterpolatedAsync", "await db.Database.ExecuteSqlInterpolatedAsync($\"DELETE FROM t_log\", ct)")]
    [InlineData("ExecuteSql", "db.Database.ExecuteSql($\"DELETE FROM t_log\")")]
    [InlineData("ExecuteSqlAsync", "await db.Database.ExecuteSqlAsync($\"DELETE FROM t_log\", ct)")]
    [InlineData("GetDbConnection", "var raw = db.Database.GetDbConnection(); ctx.Log(\"\" + raw.Database)")]
    [InlineData("SetConnectionString", "db.Database.SetConnectionString(\"Data Source=../adv.db\")")]
    [InlineData("GetConnectionString", "ctx.Log(db.Database.GetConnectionString())")]
    [InlineData("Migrate", "db.Database.Migrate()")]
    [InlineData("EnsureCreated", "db.Database.EnsureCreated()")]
    [InlineData("EnsureDeleted", "db.Database.EnsureDeleted()")]
    [InlineData("CanConnect", "ctx.Log(\"\" + db.Database.CanConnect())")]
    [InlineData("SetDbConnection", "db.Database.SetDbConnection(null)")]
    public void Escape2_EfRawSqlEntryPoints_BlockedByNamedRule(string member, string call)
    {
        var result = BuildEf($$"""
            using var db = new EscapeContext();
            {{call}};
            """);
        var expectedCodes = new[]
        {
            $"member:Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.{member}",
            $"member:Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.{member}"
        };
        Assert.False(result.Success, $"应被门禁拦截（{member}），实际：" + Describe(result));
        Assert.True(
            result.Blocked.Any(b => expectedCodes.Contains(b.Code)),
            $"期望拦截规则 {string.Join(" 或 ", expectedCodes)}，实际：" + Describe(result));
    }

    [Fact]
    public void Escape2_EfTypeAndBuilder_StillCompile()
    {
        // 正例：EF 类型本身（typeof/options 构建器）保留可编译——拦截的是连接与 SQL 入口，不是整个 EF 面
        var result = BuildEf("""
            var ef = typeof(Microsoft.EntityFrameworkCore.DbContext);
            var builder = new DbContextOptionsBuilder();
            ctx.Log($"{ef != null} {builder != null}");
            """);
        Assert.True(result.Success, "EF 类型面应保留可编译：" + Describe(result));
    }

    // ==================================================================== 逃逸 3：非 System.IO 的 BCL 文件面

    [Theory]
    [InlineData("xml-writer", "System.Xml.XmlWriter.Create(\"C:/x.txt\");", "ns:System.Xml")]
    [InlineData("xdocument-load", "var doc = System.Xml.Linq.XDocument.Load(\"C:/x.txt\"); ctx.Log(\"\" + doc);", "ns:System.Xml")]
    [InlineData("xdocument-save", "var doc = new System.Xml.Linq.XDocument(); doc.Save(\"C:/x.txt\");", "ns:System.Xml")]
    [InlineData("xmldocument", "var doc = new System.Xml.XmlDocument(); doc.Load(\"C:/x.txt\"); ctx.Log(\"\" + doc);", "ns:System.Xml")]
    [InlineData("xmlserializer", "var ser = new System.Xml.Serialization.XmlSerializer(typeof(object)); ctx.Log(\"\" + ser);", "ns:System.Xml")]
    [InlineData("webclient", "new System.Net.WebClient().DownloadFile(\"https://example.com/a\", \"C:/x.bin\");", "type:System.Net.WebClient")]
    [InlineData("textwriter-trace", "var tr = new System.Diagnostics.TextWriterTraceListener(\"C:/x.txt\"); ctx.Log(\"\" + tr);", "type:System.Diagnostics.TextWriterTraceListener")]
    [InlineData("delimited-trace", "var tr = new System.Diagnostics.DelimitedListTraceListener(\"C:/x.log\"); ctx.Log(\"\" + tr);", "type:System.Diagnostics.DelimitedListTraceListener")]
    [InlineData("xml-trace", "var tr = new System.Diagnostics.XmlWriterTraceListener(\"C:/x.log\"); ctx.Log(\"\" + tr);", "type:System.Diagnostics.XmlWriterTraceListener")]
    [InlineData("resource-writer", "var rw = new System.Resources.ResourceWriter(\"C:/x.resources\"); ctx.Log(\"\" + rw);", "ns:System.Resources")]
    [InlineData("resource-reader", "var rr = new System.Resources.ResourceReader(\"C:/x.resources\"); ctx.Log(\"\" + rr);", "ns:System.Resources")]
    [InlineData("tarfile", "System.Formats.Tar.TarFile.ExtractToDirectory(\"a.tar\", \"C:/dest\", overwriteFiles: true);", "ns:System.Formats.Tar")]
    public void Escape3_NonSystemIoFileApis_BlockedByNamedRule(string _, string body, string expectedCode)
    {
        // System.IO 只拦了「命名空间前缀」，下列 BCL 类型都可读写任意路径文件（活体证据逐条在列）
        var result = Build(null, body);
        AssertBlockedWithCode(result, expectedCode);
    }

    [Theory]
    [InlineData("HtmlDocument.Load 路径读文件", "var doc = new HtmlAgilityPack.HtmlDocument(); doc.Load(\"C:/x.html\");", "member:HtmlAgilityPack.HtmlDocument.Load")]
    [InlineData("HtmlDocument.Save 路径写文件", "var doc = new HtmlAgilityPack.HtmlDocument(); doc.Save(\"C:/x.html\");", "member:HtmlAgilityPack.HtmlDocument.Save")]
    [InlineData("HtmlWeb 网络抓取", "var web = new HtmlAgilityPack.HtmlWeb(); web.Load(\"https://example.com\");", "member:HtmlAgilityPack.HtmlWeb.Load")]
    [InlineData("HtmlWeb.Get 网络抓取", "var web = new HtmlAgilityPack.HtmlWeb(); web.Get(\"https://example.com\", \"x\");", "member:HtmlAgilityPack.HtmlWeb.Get")]
    public void Escape3_HtmlAgilityPackFileAndNetworkFaces_BlockedByNamedRule(string _, string body, string expectedCode)
    {
        // 引用白名单里的 HtmlAgilityPack 自身带文件/网络面：内存解析（LoadHtml/SelectNodes/GetAttributeValue）保留，
        // 路径与网络入口拦（dygangs.cs / dygangs_search.cs 只用 LoadHtml，不受影响）
        var result = Build(null, body);
        AssertBlockedWithCode(result, expectedCode);
    }

    // ==================================================================== 逃逸 4：反射桥（Type/Delegate 成员）

    [Fact]
    public void Escape4_ReflectionBridge_EvidenceSnippet_BlockedByNamedRule()
    {
        // 活体证据（原文）：
        //   var t = new Newtonsoft.Json.Serialization.DefaultSerializationBinder().BindToType("System.Private.CoreLib", "System.IO.File");
        //   var m = t.GetMethod("ReadAllText", new[] { typeof(string) });
        //   var f = (Func<string,string>)System.Delegate.CreateDelegate(typeof(Func<string,string>), m);
        //   ctx.Log(f("C:/Windows/win.ini"));
        // 根因：QualificationNames 只看符号自身归属链，System.Type/System.Delegate 的成员落在 System 命名空间下，
        // 命名空间前缀规则对它不可见——须补成员级规则（现状仅被 var 候选符号 MethodInfo 偶然拦住，非规则命中）。
        var result = Build(null, """
            var t = new Newtonsoft.Json.Serialization.DefaultSerializationBinder().BindToType("System.Private.CoreLib", "System.IO.File");
            var m = t.GetMethod("ReadAllText", new[] { typeof(string) });
            var f = (Func<string, string>)System.Delegate.CreateDelegate(typeof(Func<string, string>), m);
            ctx.Log(f("C:/Windows/win.ini"));
            """);
        AssertBlockedWithCode(result, "member:System.Type.GetMethod");
        Assert.Contains(result.Blocked, b => b.Code == "member:System.Delegate.CreateDelegate");
    }

    [Fact]
    public void Escape4_ReflectionBridge_InlineForm_BlockedByNamedRule()
    {
        // 不落 MethodInfo 变量（不触发 var 推导的 System.Reflection 偶然命中）：修复前完整可编译执行
        var result = Build(null, """
            var t = new Newtonsoft.Json.Serialization.DefaultSerializationBinder().BindToType("System.Private.CoreLib", "System.IO.File");
            var f = (Func<string, string>)System.Delegate.CreateDelegate(typeof(Func<string, string>), t.GetMethod("ReadAllText", new[] { typeof(string) }));
            ctx.Log(f("C:/Windows/win.ini"));
            """);
        AssertBlockedWithCode(result, "member:System.Type.GetMethod");
        Assert.Contains(result.Blocked, b => b.Code == "member:System.Delegate.CreateDelegate");
    }

    [Theory]
    [InlineData("GetMethods", "ctx.Log(\"\" + t.GetMethods().Length);", "member:System.Type.GetMethods")]
    [InlineData("GetConstructor", "ctx.Log(\"\" + (t.GetConstructor(new System.Type[] { }) != null));", "member:System.Type.GetConstructor")]
    [InlineData("GetProperties", "ctx.Log(\"\" + t.GetProperties().Length);", "member:System.Type.GetProperties")]
    [InlineData("GetProperty", "ctx.Log(\"\" + (t.GetProperty(\"x\") != null));", "member:System.Type.GetProperty")]
    [InlineData("GetFields", "ctx.Log(\"\" + t.GetFields().Length);", "member:System.Type.GetFields")]
    [InlineData("GetMembers", "ctx.Log(\"\" + t.GetMembers().Length);", "member:System.Type.GetMembers")]
    [InlineData("GetNestedTypes", "ctx.Log(\"\" + t.GetNestedTypes().Length);", "member:System.Type.GetNestedTypes")]
    [InlineData("GetInterfaces", "ctx.Log(\"\" + t.GetInterfaces().Length);", "member:System.Type.GetInterfaces")]
    [InlineData("GetEvents", "ctx.Log(\"\" + t.GetEvents().Length);", "member:System.Type.GetEvents")]
    [InlineData("Assembly", "ctx.Log(\"\" + (t.Assembly != null));", "member:System.Type.Assembly")]
    [InlineData("TypeHandle", "ctx.Log(\"\" + t.TypeHandle.Value);", "member:System.Type.TypeHandle")]
    [InlineData("InvokeMember", "t.InvokeMember(\"ReadAllText\", System.Reflection.BindingFlags.InvokeMethod, null, null, new object[] { \"C:/x\" });", "member:System.Type.InvokeMember")]
    public void Escape4_TypeCapabilityBridgeMembers_BlockedByNamedRule(string _, string body, string expectedCode)
    {
        var result = Build(null, "System.Type t = typeof(object);\n" + body);
        AssertBlockedWithCode(result, expectedCode);
    }

    [Fact]
    public void Escape4_DelegateDynamicInvokeAndCreateDelegate_BlockedByNamedRule()
    {
        var dynamicInvoke = Build(null, """
            System.Delegate d = (Func<int>)(() => 1);
            ctx.Log("" + d.DynamicInvoke());
            """);
        AssertBlockedWithCode(dynamicInvoke, "member:System.Delegate.DynamicInvoke");

        var createDelegate = Build(null, """
            var d = System.Delegate.CreateDelegate(typeof(Func<int>), typeof(string), "GetHashCode");
            ctx.Log("" + (d != null));
            """);
        AssertBlockedWithCode(createDelegate, "member:System.Delegate.CreateDelegate");
    }

    [Fact]
    public void Escape4_PreExistingBlocks_StillHold()
    {
        // 既有拦截不回退：Type.GetType(string) 与 System.Reflection 前缀（MethodInfo.Invoke 因此已拦）
        var getType = Build(null, "var t = System.Type.GetType(\"System.IO.File, System.Runtime\"); ctx.Log(\"\" + t);");
        AssertBlockedWithCode(getType, "member:System.Type.GetType");

        var methodInfoInvoke = Build(null, """
            System.Reflection.MethodInfo m = null;
            m.Invoke(null, null);
            """);
        AssertBlockedWithCode(methodInfoInvoke, "ns:System.Reflection");
    }

    // ==================================================================== 逃逸 5：Unsafe / RuntimeHelpers / MemoryMarshal

    [Fact]
    public void Escape5_UnsafeAndRuntimeHelpers_BlockedByNamedRule()
    {
        var unsafeSizeOf = Build(null, "var n = System.Runtime.CompilerServices.Unsafe.SizeOf<int>(); ctx.Log(\"\" + n);");
        AssertBlockedWithCode(unsafeSizeOf, "type:System.Runtime.CompilerServices.Unsafe");

        var runtimeHelpers = Build(null, "var o = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(object)); ctx.Log(\"\" + o);");
        AssertBlockedWithCode(runtimeHelpers, "type:System.Runtime.CompilerServices.RuntimeHelpers");
    }

    [Fact]
    public void Escape5_MemoryMarshal_BlockedByNamedRule()
    {
        // 事实核对：MemoryMarshal 的真实命名空间是 System.Runtime.InteropServices（已在命名空间前缀黑名单内）；
        // System.Buffers 下不存在该类型（CS0234）。此处锁住真实位置的拦截。
        var result = Build(null, "var m = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(new byte[4]); ctx.Log(\"\" + m.Length);");
        AssertBlockedWithCode(result, "ns:System.Runtime.InteropServices");
    }

    [Fact]
    public void Escape5_VisualBasicFileIo_BlockedByNamedRule()
    {
        // Microsoft.VisualBasic.FileIO.FileSystem 是 System.IO 的同能力替代（活体探测确认编译引用集内可用）
        var result = Build(null, """
            Microsoft.VisualBasic.FileIO.FileSystem.WriteAllText("C:/x.txt", "data", false);
            var text = Microsoft.VisualBasic.FileIO.FileSystem.ReadAllText("C:/x.txt");
            ctx.Log(text);
            """);
        AssertBlockedWithCode(result, "ns:Microsoft.VisualBasic");
    }

    // ==================================================================== 逃逸 6：System.Net 裸通道（WebSocket/SMTP/ICMP/HttpListener）

    [Theory]
    [InlineData("client-websocket", "var ws = new System.Net.WebSockets.ClientWebSocket(); ctx.Log(\"\" + ws.State);", "ns:System.Net.WebSockets")]
    [InlineData("smtp-client", "var smtp = new System.Net.Mail.SmtpClient(\"smtp.example.com\"); ctx.Log(\"\" + smtp.Host);", "ns:System.Net.Mail")]
    [InlineData("ping", "var ping = new System.Net.NetworkInformation.Ping(); ctx.Log(\"\" + ping);", "ns:System.Net.NetworkInformation")]
    [InlineData("http-listener", "var listener = new System.Net.HttpListener(); ctx.Log(\"\" + listener);", "type:System.Net.HttpListener")]
    [InlineData("dns", "var addrs = System.Net.Dns.GetHostAddresses(\"example.com\"); ctx.Log(\"\" + addrs.Length);", "type:System.Net.Dns")]
    public void Escape6_RawNetworkChannels_BlockedByNamedRule(string _, string body, string expectedCode)
    {
        // System.Net 前缀不可整段拦（WebUtility/HttpStatusCode/Http.Headers 是既有能力），故按命名空间/类型细分
        var result = Build(null, body);
        AssertBlockedWithCode(result, expectedCode);
    }

    // ==================================================================== 逃逸 7：后台调度器（记录关闭后继续运行）

    [Theory]
    [InlineData("threading-timer", "var timer = new System.Threading.Timer(_ => { }); ctx.Log(\"\" + timer);", "type:System.Threading.Timer")]
    [InlineData("timers-timer", "var timer = new System.Timers.Timer(1000); ctx.Log(\"\" + timer);", "type:System.Timers.Timer")]
    [InlineData("threadpool", "System.Threading.ThreadPool.QueueUserWorkItem(_ => { }); ctx.Log(\"ok\");", "type:System.Threading.ThreadPool")]
    public void Escape7_BackgroundSchedulers_BlockedByNamedRule(string _, string body, string expectedCode)
    {
        // 现状只拦 System.Threading.Thread 前缀：Timer/Timers.Timer/ThreadPool 都能让代码在运行记录关闭后继续跑
        var result = Build(null, body);
        AssertBlockedWithCode(result, expectedCode);
    }

    // ==================================================================== 逃逸 8：Diagnostics 调试/文件面

    [Theory]
    [InlineData("debugger", "System.Diagnostics.Debugger.Launch();", "type:System.Diagnostics.Debugger")]
    [InlineData("fileversioninfo", "var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(\"C:/Windows/win.ini\"); ctx.Log(\"\" + v);", "type:System.Diagnostics.FileVersionInfo")]
    public void Escape8_DiagnosticsApis_BlockedByNamedRule(string _, string body, string expectedCode)
    {
        var result = Build(null, body);
        AssertBlockedWithCode(result, expectedCode);
    }

    // ==================================================================== 端到端：攻击脚本在真实执行路径被拒

    [Fact]
    public async Task EndToEnd_EvidenceAttackScript_RefusedAtExecutionPath_NoFileOnDisk()
    {
        // 活体证据的完整攻击形态（写 scripts/quantum 之外的文件库 + 改平台库）：绕过无门禁的保存 API
        // 直写磁盘后执行——执行侧二次校验必须在**运行脚本之前**拒绝，且文件绝不落盘。
        var outside = Path.GetFullPath("../quantum-escape-e2e.db").Replace('\\', '/');
        var scriptDir = Path.Combine("scripts", "quantum");
        Directory.CreateDirectory(scriptDir);
        var scriptPath = Path.Combine(scriptDir, "gate_escape_e2e.cs");
        var attack = $$"""
            using Microsoft.Data.Sqlite;
            using Quantum.Plugins;
            public class EscapeE2eTask : IQuantumTask
            {
                public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                {
                    using (var conn = new SqliteConnection("Data Source={{outside}}"))
                    {
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "CREATE TABLE adv(x INTEGER); INSERT INTO adv VALUES(1);";
                        cmd.ExecuteNonQuery();
                    }
                    ctx.Log("攻击脚本居然跑起来了");
                    return Task.CompletedTask;
                }
            }
            """;
        try
        {
            File.Delete(outside);
            await File.WriteAllTextAsync(scriptPath, attack);

            var step = new TaskCommandStep
            {
                Task = new TaskModel { FileName = Path.GetFileName(scriptPath), Name = "门禁逃逸端到端" },
                CreateTime = DateTime.Now,
                ForceEndTime = DateTime.Now.AddSeconds(30),
                Envs = []
            };
            await step.Run();

            var logDir = $"./logs/{TaskExcuteService.LogDirNameFrom(Path.GetFileName(scriptPath))}";
            var logText = File.ReadAllText(Directory.GetFiles(logDir).OrderByDescending(n => n).First());

            Assert.Contains("未通过安全门禁", logText);
            Assert.Contains("仅允许「含 :memory: 的字符串字面量」实参", logText);
            Assert.DoesNotContain("攻击脚本居然跑起来了", logText);
            Assert.False(File.Exists(outside), $"攻击产物不得落盘：{outside}");
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(outside);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    // ==================================================================== 正例对照：不得过度拦截

    public static IEnumerable<object[]> PositiveControls()
    {
        // 1) 自带 demo 全接口示例.cs:177 的 SQLite :memory: 形态（必须继续编译并通过门禁）
        yield return
        [
            "sqlite-memory（demo:177 原样）",
            """
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
                """,
            "using Microsoft.Data.Sqlite;"
        ];
        // 2) :memory: 判定按大小写不敏感的字面量包含（门禁层放行；运行时语义由 SQLite 自行校验）
        yield return
        [
            "sqlite-memory-大小写",
            "using var conn = new SqliteConnection(\"Data Source=:MEMORY:\"); ctx.Log(conn.ConnectionString);",
            "using Microsoft.Data.Sqlite;"
        ];
        // 3) HtmlAgilityPack：dygangs.cs / dygangs_search.cs 的实际用法
        yield return
        [
            "html-agility（dygangs 用法）",
            """
                var doc = new HtmlDocument();
                doc.LoadHtml("<html><body><ul><li>签到</li></ul></body></html>");
                var anchors = doc.DocumentNode.SelectNodes("//a[starts-with(@href,'magnet:?xt=urn:btih:')]");
                var link = new HtmlDocument().DocumentNode.GetAttributeValue("href", null);
                ctx.Log("" + (anchors == null) + link);
                """,
            "using HtmlAgilityPack;"
        ];
        // 4) Newtonsoft：自带脚本的主力 JSON 能力
        yield return
        [
            "newtonsoft",
            """
                var jobj = new JObject { ["name"] = "quantum" };
                var parsed = JsonConvert.DeserializeObject<JObject>("{\"ok\":true}");
                ctx.Log(jobj.ToString(Formatting.None) + parsed["ok"]);
                """,
            "using Newtonsoft.Json;using Newtonsoft.Json.Linq;"
        ];
        // 5) 哈希/AES（既有测试 Build_ReferenceWhitelistLibraries_AreUsable 同款）
        yield return
        [
            "crypto",
            """
                var md5 = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes("x")));
                var sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("x")));
                using var aes = Aes.Create();
                ctx.Log($"{md5} {sha} {aes != null}");
                """,
            "using System.Security.Cryptography;"
        ];
        // 6) System.Text.Json（含 JsonElement.GetProperty——不得与 System.Type.GetProperty 混同）
        yield return
        [
            "system-text-json",
            """
                var stj = System.Text.Json.JsonSerializer.Serialize(new { a = 1 });
                using var doc = System.Text.Json.JsonDocument.Parse("{\"a\":1}");
                ctx.Log(stj + doc.RootElement.GetProperty("a").GetInt32());
                """,
            null
        ];
        // 7) LINQ / 委托自带方法（Func.Invoke 不得被 Delegate 成员规则误伤）
        yield return
        [
            "linq-and-func",
            """
                var list = new[] { 1, 2, 3 }.Select(x => x * 2).Where(x => x > 2).ToList();
                Func<int, int> f = x => x + 1;
                ctx.Log("" + list.Count + f(1));
                """,
            null
        ];
        // 8) System.Formats.Asn1：acme_cert.cs 实际用法（不得被 System.Formats.Tar 前缀误伤）
        yield return
        [
            "asn1（acme_cert 用法）",
            """
                var reader = new AsnReader(new byte[] { 1 }, AsnEncodingRules.DER);
                ctx.Log("" + (reader != null));
                """,
            "using System.Formats.Asn1;"
        ];
        // 9) System.Net 既有能力：WebUtility / HttpStatusCode / Http.Headers（不得被裸通道规则误伤）
        yield return
        [
            "net-utility",
            """
                var html = System.Net.WebUtility.HtmlDecode("&amp;");
                var code = System.Net.HttpStatusCode.Conflict;
                var header = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");
                ctx.Log($"{html} {code} {header}");
                """,
            null
        ];
        // 10) Diagnostics.Stopwatch：demo 第 10 节用法
        yield return
        [
            "stopwatch（demo 用法）",
            "var sw = System.Diagnostics.Stopwatch.StartNew(); sw.Stop(); ctx.Log(\"\" + sw.ElapsedMilliseconds);",
            null
        ];
        // 11) 实例 GetType()（object 返回）：仅 Type 成员面关闭，不得扩大为按成员名全拦
        yield return
        [
            "instance-gettype",
            """
                try { throw new InvalidOperationException("x"); }
                catch (Exception e) { var t = e.GetType(); ctx.Log("" + (t != null)); }
                """,
            null
        ];
        // 12) 协作取消与并发基础（Task/ct）
        yield return
        [
            "task-and-ct",
            """
                await Task.Delay(1, ct);
                var t = Task.Run(() => 1, ct);
                ctx.Log("" + await t);
                """,
            null
        ];
    }

    [Theory]
    [MemberData(nameof(PositiveControls))]
    public void PositiveControls_StillPassGateAndCompile(string _, string body, string usings)
    {
        var result = Build(usings, body);
        Assert.True(result.Success, "正例不得被拦截：" + Describe(result));
        Assert.Empty(result.Blocked);
    }

    // ==================================================================== 测试工具

    /// <summary>用生产同款引用集与前置（global using + #line 1）拼接完整脚本后跑 Build。</summary>
    private static ScriptBuildService.BuildResult Build(string usings, string body, string extraTypes = null)
    {
        var source = (string.IsNullOrEmpty(usings) ? string.Empty : usings + "\n")
            + "using Quantum.Plugins;\n"
            + (string.IsNullOrEmpty(extraTypes) ? string.Empty : extraTypes + "\n")
            + "public class GateEscapeTask : IQuantumTask\n{\n"
            + "    public async Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)\n    {\n"
            + body.TrimEnd('\r', '\n')
            + "\n        await Task.CompletedTask;\n    }\n}\n";
        return ScriptBuildService.Build(source);
    }

    /// <summary>EF 用例：附一个可实例化的 DbContext 派生类（隐含公共无参构造）。</summary>
    private static ScriptBuildService.BuildResult BuildEf(string body)
    {
        return Build("using Microsoft.EntityFrameworkCore;", body,
            extraTypes: "public class EscapeContext : DbContext { }\n"
                + "public class EscapeContextWithOptions : DbContext { public EscapeContextWithOptions(DbContextOptions options) : base(options) { } }");
    }

    private static void AssertBlockedWithCode(ScriptBuildService.BuildResult result, string expectedCode)
    {
        Assert.False(result.Success, $"应被门禁拦截（Success=false），实际：" + Describe(result));
        Assert.True(
            result.Blocked.Any(b => b.Code == expectedCode),
            $"期望拦截规则 {expectedCode}，实际：" + Describe(result));
    }

    private static string Describe(ScriptBuildService.BuildResult result)
        => "blocked=[" + string.Join(", ", result.Blocked.Select(b => $"{b.Code}@L{b.Line}"))
           + "] errors=[" + string.Join(", ", result.Errors.Select(e => $"{e.Code}@L{e.Line}")) + "]";
}
