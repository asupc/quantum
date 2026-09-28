using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.DTOs;
using Quantum.Entities.Model;

namespace Quantum.API.Tests;

/// <summary>指令入口的运行记录必须在有效的 DI 作用域内完成领取，且脚本执行后落终态。</summary>
[Collection("ConstsState")]
public class TaskRunRecorderTests
{
    [Fact]
    public async Task RunStepAsync_Command_ClaimsBeforeScopeIsDisposed_AndCompletes()
    {
        var (connection, setupDb) = AppTestDb.Create();
        using (connection)
        using (setupDb)
        {
            var options = new DbContextOptionsBuilder<QuantumSqliteDbContext>()
                .UseSqlite(connection).Options;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped(_ => new QuantumSqliteDbContext(options));
            services.AddScoped<IQuantumDbContext>(sp => sp.GetRequiredService<QuantumSqliteDbContext>());
            services.AddScoped<TaskRunService>();
            services.AddScoped<TaskAlertService>();
            using var provider = services.BuildServiceProvider(validateScopes: true);
            TaskRunRecorder.Init(provider.GetRequiredService<IServiceScopeFactory>());
            try
            {
                var step = new TaskCommandStep
                {
                    Task = new TaskModel
                    {
                        Id = "command-scope-test",
                        Name = "指令作用域回归",
                        FileName = "missing_command_scope_regression.cs"
                    },
                    Envs = [],
                    CreateTime = DateTime.Now
                };

                // 脚本不存在时执行器应给出真实拒绝终态；此前这里会在 ClaimAsync 抛
                // ObjectDisposedException，既不执行脚本，也不会写入运行日志。
                var result = await TaskRunRecorder.RunStepAsync(step, TaskTriggerSource.Command,
                    "admin", LogType.指令触发);

                Assert.Equal(TaskExecutionOutcome.Rejected, result.Outcome);
                var run = Assert.Single(await setupDb.TaskRuns.AsNoTracking().ToListAsync());
                Assert.Equal(TaskRunStatus.Rejected, run.Status);
                Assert.Equal(TaskTriggerSource.Command, run.TriggerSource);
                var log = Assert.Single(await setupDb.Logs.AsNoTracking().ToListAsync());
                Assert.Equal(run.LogId, log.Id);
                Assert.False(log.Success);
            }
            finally
            {
                TaskRunRecorder.Init(null);
            }
        }
    }
}
