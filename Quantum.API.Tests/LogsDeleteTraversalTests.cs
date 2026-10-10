using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 日志批量删除的路径越狱回归（对抗性审计 FIX-2）。
///
/// 删除侧旧实现直接 <c>File.Delete("./logs/" + LogPath)</c>，与已加固的读取侧
/// （<c>SafeFile.Resolve</c>）形成不对称防御：存量脏行/导入行的 LogPath 含 "../" 即可删掉
/// ./logs 之外的文件。本组用例钉死删除侧必须与读取侧同款收口，且单条脏行不得阻断其余行的删除。
/// 目录基准为测试进程当前目录（bin 输出目录），路径与断言都按当前目录解析。
/// </summary>
public sealed class LogsDeleteTraversalTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;

    public LogsDeleteTraversalTests()
    {
        (_connection, _db) = AppTestDb.Create();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>越狱目标路径：与旧实现 "./logs/" + "../../evil.txt" 落点完全一致。</summary>
    private static string TraversalTarget(string name)
        => Path.GetFullPath(Path.Combine("./logs", "..", "..", name));

    /// <summary>落库后清跟踪：DeleteAsync 内部 AsNoTracking 查询再 RemoveRange，
    /// 与本类种子行的跟踪实例同 Id 会撞 EF 身份映射（生产是每请求独立 scope，无此态）。</summary>
    private void Detach() => _db.ChangeTracker.Clear();

    [Fact]
    public async Task Delete_TraversalLogPath_LeavesOutsideFileIntact_AndStillDeletesLegitFile()
    {
        // 越狱诱饵：与 ./logs 同源的相对穿越落点
        var decoy = TraversalTarget("quantum-traversal-decoy.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(decoy)!);
        await File.WriteAllTextAsync(decoy, "OUTSIDE");

        // 同批合法行：删除侧加固后仍须被正常清理（脏行不得让整批删除变成静默 no-op）
        var dirName = "delguard_" + Guid.NewGuid().ToString("N");
        var legitDir = Path.Combine("logs", dirName);
        Directory.CreateDirectory(legitDir);
        var legit = Path.Combine(legitDir, "ok.log");
        await File.WriteAllTextAsync(legit, "INSIDE");

        _db.Logs.Add(new LogModel
        {
            Id = "DEL1", CreateTime = DateTime.Now, LogType = LogType.任务日志, Title = "穿越行",
            LogPath = "../../" + Path.GetFileName(decoy)
        });
        _db.Logs.Add(new LogModel
        {
            Id = "DEL2", CreateTime = DateTime.Now, LogType = LogType.任务日志, Title = "合法行",
            DirectoryName = dirName, LogPath = "ok.log"
        });
        await _db.SaveChangesAsync();
        Detach();

        try
        {
            var service = new LogsService(_db);
            Assert.True(await service.DeleteAsync("DEL1,DEL2"));

            Assert.True(File.Exists(decoy), "./logs 之外的文件不得被日志删除链路删掉");
            Assert.False(File.Exists(legit), "logs 根内的合法日志文件仍须被删除");
            Assert.Equal(0, await _db.Logs.CountAsync());
        }
        finally
        {
            try { File.Delete(decoy); } catch { /* 清理失败不影响结论 */ }
            try { Directory.Delete(legitDir, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }
    }

    [Fact]
    public async Task Delete_TraversalDirectoryName_DoesNotDeleteOutsideFile()
    {
        // DirectoryName 含穿越片段（读取侧已有同款用例）：删除侧拼接同样要收口
        var decoy = TraversalTarget("quantum-traversal-dir-decoy.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(decoy)!);
        await File.WriteAllTextAsync(decoy, "OUTSIDE");

        _db.Logs.Add(new LogModel
        {
            Id = "DEL3", CreateTime = DateTime.Now, LogType = LogType.任务日志, Title = "穿越目录行",
            DirectoryName = "../..", LogPath = Path.GetFileName(decoy)
        });
        await _db.SaveChangesAsync();
        Detach();

        try
        {
            var service = new LogsService(_db);
            Assert.True(await service.DeleteAsync("DEL3"));

            Assert.True(File.Exists(decoy), "穿越的 DirectoryName 不得把删除动作带出 ./logs");
        }
        finally
        {
            try { File.Delete(decoy); } catch { /* 清理失败不影响结论 */ }
        }
    }

    [Fact]
    public async Task Delete_LegitimateSubDirectoryRow_DeletesFile()
    {
        // 正向基线：logs/<dir>/<file> 的正常行必须照常删除（加固不得误伤合法路径）
        var dirName = "delok_" + Guid.NewGuid().ToString("N");
        var logDir = Path.Combine("logs", dirName);
        Directory.CreateDirectory(logDir);
        var file = Path.Combine(logDir, "20260101000000000.log");
        await File.WriteAllTextAsync(file, "LEGIT");

        _db.Logs.Add(new LogModel
        {
            Id = "DEL4", CreateTime = DateTime.Now, LogType = LogType.任务日志, Title = "合法行",
            DirectoryName = dirName, LogPath = Path.GetFileName(file)
        });
        await _db.SaveChangesAsync();
        Detach();

        try
        {
            var service = new LogsService(_db);
            Assert.True(await service.DeleteAsync("DEL4"));

            Assert.False(File.Exists(file));
        }
        finally
        {
            try { Directory.Delete(logDir, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }
    }
}
