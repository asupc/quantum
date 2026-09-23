using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.API.Tests;

/// <summary>
/// 上传服务：白名单、大小上限、下载存在性校验；20 次/分钟限流。
/// </summary>
public class AppUploadServiceTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly QuantumSqliteDbContext _db;
    private readonly AppUploadService _service;

    public AppUploadServiceTests()
    {
        (_connection, _db) = AppTestDb.Create();
        _service = new AppUploadService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static FormFileCollection Collection(params FormFile[] files) => [.. files];

    private static FormFile MakeFile(string name, byte[] content)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", name);
    }

    [Fact]
    public async Task SaveImageAsync_Rejects_Non_Whitelisted_Extension()
    {
        await Assert.ThrowsAsync<BusinessException>(() =>
            _service.SaveImageAsync(Collection(MakeFile("shell.svg", [1, 2, 3]))));
        await Assert.ThrowsAsync<BusinessException>(() =>
            _service.SaveImageAsync(Collection(MakeFile("script.exe", [1, 2, 3]))));
    }

    [Fact]
    public async Task SaveImageAsync_Rejects_Oversize()
    {
        var big = new byte[AppUploadService.MaxImageBytes + 1];
        await Assert.ThrowsAsync<BusinessException>(() =>
            _service.SaveImageAsync(Collection(MakeFile("big.png", big))));
    }

    [Fact]
    public async Task SaveImageAsync_Succeeds_And_Persists_Record()
    {
        var result = await _service.SaveImageAsync(Collection(MakeFile("cat.PNG", [1, 2, 3, 4])));

        Assert.False(string.IsNullOrEmpty(result.FileId));
        Assert.Equal("cat.PNG", result.FileName);
        Assert.Equal(4L, result.Size);

        var stored = await _db.AppFiles.AsNoTracking().SingleAsync(n => n.Id == result.FileId);
        Assert.Equal("image/png", stored.ContentType);
        Assert.Equal(".png", stored.Ext);
        // 单管理员：统一落在 app/ 子目录
        Assert.StartsWith("./db/appfiles/app/", stored.Path.Replace('\\', '/'));
    }

    [Fact]
    public async Task SaveFileAsync_Allows_Pdf_And_Rejects_Images()
    {
        var result = await _service.SaveFileAsync(Collection(MakeFile("doc.pdf", [9, 9])));
        Assert.Equal("application/pdf", (await _db.AppFiles.AsNoTracking().SingleAsync(n => n.Id == result.FileId)).ContentType);

        // 图片扩展名不属于附件白名单
        await Assert.ThrowsAsync<BusinessException>(() =>
            _service.SaveFileAsync(Collection(MakeFile("pic.webp", [1]))));
    }

    [Fact]
    public async Task GetForDownloadAsync_ReturnsSavedFile_AndRejectsUnknownId()
    {
        var result = await _service.SaveImageAsync(Collection(MakeFile("cat.png", [1])));

        var (file, _) = await _service.GetForDownloadAsync(result.FileId);
        Assert.Equal(result.FileId, file.Id);

        await Assert.ThrowsAsync<BusinessException>(() =>
            _service.GetForDownloadAsync("not-exists"));
    }

    [Fact]
    public void UploadRateLimit_Allows_20_Per_Key_Per_Minute()
    {
        var key = "rate-" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < MemoryObjectCache.AppUploadLimitPerMinute; i++)
        {
            Assert.True(MemoryObjectCache.TryAcquireAppUploadSlot(key), $"第 {i + 1} 次应放行");
        }
        Assert.False(MemoryObjectCache.TryAcquireAppUploadSlot(key));
        // 其他 key 不受影响
        Assert.True(MemoryObjectCache.TryAcquireAppUploadSlot(key + "-b"));
    }

    [Fact]
    public void ResolveContentType_Is_Fixed_By_Extension()
    {
        Assert.Equal("image/jpeg", AppUploadService.ResolveContentType(".jpg"));
        Assert.Equal("image/png", AppUploadService.ResolveContentType(".png"));
        Assert.Equal("application/pdf", AppUploadService.ResolveContentType(".pdf"));
        Assert.Equal("application/octet-stream", AppUploadService.ResolveContentType(".unknown"));
    }
}
