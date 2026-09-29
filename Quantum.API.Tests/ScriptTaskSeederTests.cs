using Microsoft.EntityFrameworkCore;
using Quantum.Web;

namespace Quantum.API.Tests;

public class ScriptTaskSeederTests
{
    private const string ValidScript = "using Quantum.Plugins; public class SeedTask : IQuantumTask { public Task RunAsync(QuantumTaskContext ctx, CancellationToken ct) => Task.CompletedTask; }";

    [Fact]
    public void CheckedInScripts_AreSeededAsDisabledRestrictedTasks()
    {
        var source = Path.Combine(FindRepoRoot(), "Quantum.API", "Quantum.Web", "scripts");
        var runtime = Path.Combine(Path.GetTempPath(), "quantum-seed-" + Guid.NewGuid().ToString("N"));
        using var connectionAndDb = new TestDatabase();
        try
        {
            ScriptTaskSeeder.Seed(connectionAndDb.Db, source, runtime);
            connectionAndDb.Db.SaveChanges();

            var names = Directory.GetFiles(source, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var tasks = connectionAndDb.Db.Tasks.AsNoTracking().ToList();
            Assert.Equal(names, tasks.Select(task => task.FileName).OrderBy(name => name, StringComparer.Ordinal));
            Assert.All(tasks, task =>
            {
                Assert.Equal(Path.GetFileNameWithoutExtension(task.FileName), task.Name);
                Assert.False(task.Enable);
                Assert.True(File.Exists(Path.Combine(runtime, task.FileName)));
            });
        }
        finally
        {
            if (Directory.Exists(runtime)) Directory.Delete(runtime, recursive: true);
        }
    }

    [Fact]
    public void ExistingRuntimeScript_IsPreserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "quantum-seed-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(runtime);
        using var connectionAndDb = new TestDatabase();
        try
        {
            File.WriteAllText(Path.Combine(source, "test.cs"), ValidScript);
            var existing = ValidScript + "\n// 本地修改";
            File.WriteAllText(Path.Combine(runtime, "test.cs"), existing);

            ScriptTaskSeeder.Seed(connectionAndDb.Db, source, runtime);

            Assert.Equal(existing, File.ReadAllText(Path.Combine(runtime, "test.cs")));
            Assert.Single(connectionAndDb.Db.Tasks.Local);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InvalidScript_DoesNotCopyOrRegisterAnyTask()
    {
        var root = Path.Combine(Path.GetTempPath(), "quantum-seed-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(source);
        using var connectionAndDb = new TestDatabase();
        try
        {
            File.WriteAllText(Path.Combine(source, "a.cs"), ValidScript);
            File.WriteAllText(Path.Combine(source, "b.cs"), "public class Broken {");

            Assert.Throws<InvalidOperationException>(() => ScriptTaskSeeder.Seed(connectionAndDb.Db, source, runtime));

            Assert.Empty(connectionAndDb.Db.Tasks.Local);
            Assert.False(Directory.Exists(runtime));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Quantum.API", "Quantum.API.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("未找到仓库根目录");
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        public Quantum.Data.QuantumSqliteDbContext Db { get; }

        public TestDatabase()
        {
            (_connection, Db) = AppTestDb.Create();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }
}
