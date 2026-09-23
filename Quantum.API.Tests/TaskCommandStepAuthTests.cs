using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;
using Quantum.Utils;
using Xunit;

namespace Quantum.API.Tests;

/// <summary>
/// P5a §1-4（Envs 换引用并发安全）与 P6/§9.2（指令步骤 threadId 路由 + 命中后按所属任务 Manager 反查鉴权）。
/// 并入 ConstsState 全局静态串行集合（§5-3）：这些用例读写 MemoryObjectCache / CacheManager 全局静态，必须与同类测试串行。
/// </summary>
[Collection("ConstsState")]
public class TaskCommandStepAuthTests : IDisposable
{
    private readonly TaskService _service = new(null!, null!, null!);
    private readonly List<string> _registeredKeys = [];

    public void Dispose()
    {
        foreach (var key in _registeredKeys)
        {
            MemoryObjectCache.TaskCommandSteps.TryRemove(key, out _);
        }
        CacheManager.Set(new List<TaskModel>());
        CacheManager.Set(new List<TaskSubModel>());
    }

    private string Register(TaskCommandStep step)
    {
        var threadId = Guid.NewGuid().ToString();
        MemoryObjectCache.TaskCommandSteps[threadId] = step;
        _registeredKeys.Add(threadId);
        return threadId;
    }

    [Fact]
    public void Finish_ForeignManagerTaskStep_NonManagerToken_ReturnsUnauthorized()
    {
        CacheManager.Set(new List<TaskModel> { new() { Id = "MGR-1", Manager = true } });
        var threadId = Register(new TaskCommandStep { Task = new TaskModel { Id = "MGR-1", Manager = true } });

        Assert.Throws<UnauthorizedBusinessException>(() => _service.Finish(threadId, isManager: false));
        // 鉴权失败不得移除步骤（否则非 Manager 令牌可 Denial-of-Service 掉他人活跃步骤）
        Assert.True(MemoryObjectCache.TaskCommandSteps.ContainsKey(threadId));
    }

    [Fact]
    public void Finish_ManagerToken_RemovesStep()
    {
        CacheManager.Set(new List<TaskModel> { new() { Id = "MGR-1", Manager = true } });
        var threadId = Register(new TaskCommandStep { Task = new TaskModel { Id = "MGR-1", Manager = true } });

        Assert.True(_service.Finish(threadId, isManager: true));
        Assert.False(MemoryObjectCache.TaskCommandSteps.ContainsKey(threadId));
    }

    [Fact]
    public void Finish_UnknownThreadId_BusinessErrorNotSilentSuccess()
    {
        var ex = Assert.Throws<BusinessException>(() => _service.Finish("no-such-thread", isManager: true));
        Assert.IsNotType<UnauthorizedBusinessException>(ex); // 明确是业务错误「不存在或已结束」，非 401
    }

    [Fact]
    public void Finish_SameTaskTwoThreads_OnlyAffectsTargetThread()
    {
        // 回归保护：同任务被两条入站消息各建一个 ThreadId，操作 A 不得波及 B
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Manager = false } });
        var a = Register(new TaskCommandStep { Task = new TaskModel { Id = "T1" } });
        var b = Register(new TaskCommandStep { Task = new TaskModel { Id = "T1" } });

        Assert.True(_service.Finish(a, isManager: true));

        Assert.False(MemoryObjectCache.TaskCommandSteps.ContainsKey(a));
        Assert.True(MemoryObjectCache.TaskCommandSteps.ContainsKey(b));
    }

    [Fact]
    public void AddEnv_SwapsEnvsReference_LeavesOldSnapshotIntact()
    {
        // §1-4：AddEnv 不得就地改共享 List，读者持有的旧引用应保持一致快照
        CacheManager.Set(new List<TaskModel> { new() { Id = "T1", Manager = false } });
        var original = new List<EnvModel> { new() { Name = "K1", Value = "v1" } };
        var step = new TaskCommandStep { Task = new TaskModel { Id = "T1" }, Envs = original };
        var threadId = Register(step);

        var result = _service.AddEnv(threadId, new EnvModel { Name = "K1", Value = "v2" }, isManager: true);

        Assert.Contains("移除同名环境变量：【1】", result);
        Assert.NotSame(original, step.Envs);            // 换引用而非原地改
        Assert.Single(original);                          // 旧快照未被污染
        Assert.Equal("v1", original[0].Value);
        Assert.Single(step.Envs);                         // 新列表：同名替换后仅一条
        Assert.Equal("v2", step.Envs[0].Value);
    }

    [Fact]
    public void AddEnv_UnknownThreadId_BusinessError()
    {
        Assert.Throws<BusinessException>(() =>
            _service.AddEnv("no-such-thread", new EnvModel { Name = "OK", Value = "v" }, isManager: true));
    }
}
