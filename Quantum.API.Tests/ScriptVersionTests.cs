using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Web.Controllers;
using Quantum.Web.Filters;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// 脚本版本管理（2026-09-20 AI 脚本修复 Agent 计划阶段一）回归：
/// 版本留痕（在线保存/上传两条写入点）、同内容去重、保留数清理与锁定、回滚（含门禁复检）、
/// 路径映射（仅 scripts/quantum 下的任务脚本参与版本管理）与控制器权限。
/// 与 ScriptEngineTests 同 Collection：共用 ScriptBuildService 静态编译缓存/门禁状态，须串行。
/// </summary>
[Collection("ConstsState")]
public class ScriptVersionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly ScriptVersionService _service;

    public ScriptVersionTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _service = new ScriptVersionService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private const string TaskSourceV1 = """
        using Quantum.Plugins;
        public class VersionTask : IQuantumTask
        {
            public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
            {
                ctx.Log("v1");
                return Task.CompletedTask;
            }
        }
        """;

    private static string TaskSourceV2 => TaskSourceV1.Replace("v1", "v2");

    private static string ScriptPath(string fileName) => Path.Combine("scripts", "quantum", fileName);

    private static void WriteScript(string fileName, string content)
    {
        Directory.CreateDirectory(Path.Combine("scripts", "quantum"));
        File.WriteAllText(ScriptPath(fileName), content);
    }

    // ==================================================================== 版本留痕

    [Fact]
    public async Task Record_SameContent_Skipped()
    {
        var first = await _service.RecordAsync("t1.cs", TaskSourceV1, ScriptVersionSource.ManualEdit);
        var second = await _service.RecordAsync("t1.cs", TaskSourceV1, ScriptVersionSource.ManualEdit);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, await _db.ScriptVersions.CountAsync(n => n.FileName == "t1.cs"));
    }

    [Fact]
    public async Task Record_TrailingNewlines_DoNotCreateDuplicateVersion()
    {
        // 在线保存落盘会补一个换行、上传读的是原始流：同一份内容不该因尾部换行差异重复留痕
        await _service.RecordAsync("t2.cs", TaskSourceV1, ScriptVersionSource.ManualEdit);
        var second = await _service.RecordAsync("t2.cs", TaskSourceV1 + "\r\n", ScriptVersionSource.Upload);

        Assert.Null(second);
        Assert.Equal(ScriptVersionService.HashOf(TaskSourceV1), ScriptVersionService.HashOf(TaskSourceV1 + "\n"));
    }

    [Fact]
    public async Task Record_ChangedContent_AddsVersionWithMetadata()
    {
        await _service.RecordAsync("t3.cs", TaskSourceV1, ScriptVersionSource.ManualEdit);
        var second = await _service.RecordAsync("t3.cs", TaskSourceV2, ScriptVersionSource.Upload, "上传覆盖", "admin");

        Assert.NotNull(second);
        Assert.Equal(ScriptVersionService.HashOf(TaskSourceV2), second.Hash);
        Assert.Equal(ScriptVersionSource.Upload, second.Source);
        Assert.Equal("上传覆盖", second.Remark);
        Assert.Equal("admin", second.Creator);
        Assert.True(second.Size > 0);
        Assert.True(second.LineCount > 1);

        var page = await _service.GetPageAsync("t3.cs");
        Assert.Equal(2, page.TotalCount);
        // 最新版本在前
        Assert.Equal(second.Id, page.Data[0].Id);
    }

    // ==================================================================== 两个写入点接线

    [Fact]
    public async Task UpdateScript_RecordsManualEditVersion()
    {
        var service = new TaskService(_db, new AppMessageService(_db, NullLogger<AppMessageService>.Instance), _service);
        var result = await service.UpdateScriptAsync(new SaveQLFile { path = "version_write_point.cs", content = TaskSourceV1 });

        Assert.True(result.Success);
        var version = await _db.ScriptVersions.SingleAsync(n => n.FileName == "version_write_point.cs");
        Assert.Equal(ScriptVersionSource.ManualEdit, version.Source);
        Assert.Equal(ScriptVersionService.HashOf(TaskSourceV1), version.Hash);
    }

    [Fact]
    public async Task UpdateScript_Blocked_RecordsNothing()
    {
        var service = new TaskService(_db, new AppMessageService(_db, NullLogger<AppMessageService>.Instance), _service);
        var result = await service.UpdateScriptAsync(new SaveQLFile
        {
            path = "version_blocked.cs",
            content = """
                using System.IO;
                using Quantum.Plugins;
                public class X : IQuantumTask
                {
                    public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                    {
                        File.ReadAllText("c:/secret");
                        return Task.CompletedTask;
                    }
                }
                """
        });

        Assert.False(result.Success);
        Assert.Equal(0, await _db.ScriptVersions.CountAsync(n => n.FileName == "version_blocked.cs"));
    }

    [Fact]
    public async Task UploadScripts_RecordsVersionOnlyUnderQuantumDir()
    {
        var service = new UploadService(_service);

        // scripts/quantum 下的任务脚本：留痕
        await service.UploadScripts(Files("version_upload.cs", TaskSourceV1), "quantum");
        Assert.Equal(1, await _db.ScriptVersions.CountAsync(n => n.FileName == "version_upload.cs"));

        // 其它目录（非任务脚本目录）：不留痕
        await service.UploadScripts(Files("version_upload_other.cs", TaskSourceV1), "version_other");
        Assert.Equal(0, await _db.ScriptVersions.CountAsync(n => n.FileName == "version_other/version_upload_other.cs"));
    }

    [Fact]
    public async Task UploadScripts_SubDirUnderQuantum_KeepsRelativePathAsVersionKey()
    {
        var service = new UploadService(_service);
        await service.UploadScripts(Files("version_sub.cs", TaskSourceV1), "quantum/open-trigger-task");

        Assert.Equal(1, await _db.ScriptVersions.CountAsync(n => n.FileName == "open-trigger-task/version_sub.cs"));
    }

    // ==================================================================== 回滚

    [Fact]
    public async Task Rollback_RestoresContentAndRecordsRollbackVersion()
    {
        var taskService = new TaskService(_db, new AppMessageService(_db, NullLogger<AppMessageService>.Instance), _service);
        await taskService.UpdateScriptAsync(new SaveQLFile { path = "version_rollback.cs", content = TaskSourceV1 });
        var v1 = await _db.ScriptVersions.AsNoTracking().SingleAsync(n => n.FileName == "version_rollback.cs");

        await taskService.UpdateScriptAsync(new SaveQLFile { path = "version_rollback.cs", content = TaskSourceV2 });
        Assert.Contains("v2", await File.ReadAllTextAsync(ScriptPath("version_rollback.cs")));

        var result = await _service.RollbackAsync(v1.Id, "admin");

        Assert.True(result.Success);
        Assert.Contains("v1", await File.ReadAllTextAsync(ScriptPath("version_rollback.cs")));
        var versions = await _db.ScriptVersions.AsNoTracking()
            .Where(n => n.FileName == "version_rollback.cs").ToListAsync();
        Assert.Equal(3, versions.Count);
        Assert.Contains(versions, n => n.Source == ScriptVersionSource.Rollback);
        // 回滚版本内容 = 被回滚到的历史内容
        Assert.Equal(ScriptVersionService.HashOf(TaskSourceV1),
            versions.Single(n => n.Source == ScriptVersionSource.Rollback).Hash);
    }

    [Fact]
    public async Task Rollback_BlockedHistoricalContent_RejectedWithoutDiskWrite()
    {
        // 历史版本本身不过门禁（模拟门禁规则收紧后的老版本）：回滚必须被拒且不落盘
        var version = new ScriptVersionModel
        {
            FileName = "version_rollback_blocked.cs",
            Hash = ScriptVersionService.HashOf("using System.IO;"),
            Content = """
                using System.IO;
                using Quantum.Plugins;
                public class X : IQuantumTask
                {
                    public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct)
                    {
                        File.ReadAllText("c:/secret");
                        return Task.CompletedTask;
                    }
                }
                """,
            Source = ScriptVersionSource.ManualEdit,
            CreateTime = DateTime.Now
        };
        _db.ScriptVersions.Add(version);
        await _db.SaveChangesAsync();
        WriteScript("version_rollback_blocked.cs", TaskSourceV1);

        var result = await _service.RollbackAsync(version.Id);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Blocked);
        Assert.Equal(ScriptVersionService.Normalize(TaskSourceV1),
            ScriptVersionService.Normalize(await File.ReadAllTextAsync(ScriptPath("version_rollback_blocked.cs"))));
    }

    // ==================================================================== 保留策略与其它

    [Fact]
    public async Task Prune_KeepsLatestNonPinnedVersions()
    {
        for (var i = 0; i < ScriptVersionService.KeepPerFile + 5; i++)
        {
            await _service.RecordAsync("version_prune.cs", $"{TaskSourceV1}\n// rev {i}", ScriptVersionSource.ManualEdit);
        }

        Assert.Equal(ScriptVersionService.KeepPerFile,
            await _db.ScriptVersions.CountAsync(n => n.FileName == "version_prune.cs"));
    }

    [Fact]
    public async Task Prune_DoesNotTouchPinnedVersion()
    {
        var pinned = await _service.RecordAsync("version_pin.cs", TaskSourceV1, ScriptVersionSource.ManualEdit);
        Assert.True(await _service.SetPinnedAsync(pinned.Id, true));

        for (var i = 0; i < ScriptVersionService.KeepPerFile + 5; i++)
        {
            await _service.RecordAsync("version_pin.cs", $"{TaskSourceV1}\n// rev {i}", ScriptVersionSource.ManualEdit);
        }

        Assert.Equal(1, await _db.ScriptVersions.CountAsync(n => n.Id == pinned.Id && n.IsPinned));
        Assert.True(await _db.ScriptVersions.CountAsync(n => n.FileName == "version_pin.cs") > ScriptVersionService.KeepPerFile);
    }

    [Fact]
    public void ToQuantumRelative_MapsOnlyQuantumDir()
    {
        Assert.Equal("B站任务.cs", ScriptVersionService.ToQuantumRelative("quantum/B站任务.cs"));
        Assert.Equal("open-trigger-task/x.cs", ScriptVersionService.ToQuantumRelative(@"quantum\open-trigger-task\x.cs"));
        Assert.Equal("x.cs", ScriptVersionService.ToQuantumRelative("QUANTUM/x.cs"));
        Assert.Null(ScriptVersionService.ToQuantumRelative("demo/x.cs"));
        Assert.Null(ScriptVersionService.ToQuantumRelative("x.cs"));
        Assert.Null(ScriptVersionService.ToQuantumRelative(null));
    }

    [Fact]
    public async Task GetFiles_GroupsByFileName()
    {
        await _service.RecordAsync("version_files_a.cs", TaskSourceV1, ScriptVersionSource.ManualEdit);
        await _service.RecordAsync("version_files_a.cs", TaskSourceV2, ScriptVersionSource.ManualEdit);
        await _service.RecordAsync("version_files_b.cs", TaskSourceV1, ScriptVersionSource.Upload);

        var files = await _service.GetFilesAsync();

        Assert.Equal(2, files.Count);
        Assert.Equal(2, files.Single(n => n.FileName == "version_files_a.cs").Count);
        Assert.Equal(1, files.Single(n => n.FileName == "version_files_b.cs").Count);
    }

    [Fact]
    public void ScriptVersionController_IsManagerOnly()
    {
        // 版本历史含脚本全文与密钥扫描结果，必须与脚本编辑同权（管理员专属）
        var type = typeof(ScriptVersionController);
        Assert.NotNull(type.GetCustomAttributes(typeof(ManagerOnlyAttribute), inherit: true).SingleOrDefault());
        Assert.NotNull(type.GetCustomAttributes(typeof(CustomAuthorizationFilter), inherit: true).SingleOrDefault());
    }

    // ==================================================================== 测试工具

    private static Microsoft.AspNetCore.Http.IFormFileCollection Files(string fileName, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var collection = new Microsoft.AspNetCore.Http.FormFileCollection
        {
            new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        };
        return collection;
    }
}
