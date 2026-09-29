using Quantum.Application;
using Quantum.Data;
using Quantum.Entities.Model;

namespace Quantum.Web;

/// <summary>仅在新库初始化时，将随应用分发的正式脚本登记为待配置任务。</summary>
public static class ScriptTaskSeeder
{
    public static void Seed(IQuantumDbContext db, string sourceDirectory, string runtimeDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"任务种子脚本目录不存在：{sourceDirectory}");
        }

        var scripts = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        if (scripts.Count == 0)
        {
            throw new InvalidOperationException($"任务种子脚本目录没有 .cs 文件：{sourceDirectory}");
        }

        var validated = new List<(string SourcePath, string FileName, string RuntimePath)>();
        foreach (var sourcePath in scripts)
        {
            var fileName = Path.GetFileName(sourcePath);
            var runtimePath = Path.Combine(runtimeDirectory, fileName);
            var content = File.ReadAllText(File.Exists(runtimePath) ? runtimePath : sourcePath);
            var build = ScriptBuildService.Build(content, runtimePath);
            if (!build.Success)
            {
                var issues = build.Blocked.Concat(build.Errors).Select(issue => issue.Message);
                throw new InvalidOperationException($"任务种子脚本 {fileName} 未通过门禁或编译：{string.Join("；", issues)}");
            }

            validated.Add((sourcePath, fileName, runtimePath));
        }

        Directory.CreateDirectory(runtimeDirectory);
        foreach (var (sourcePath, fileName, runtimePath) in validated)
        {
            if (!File.Exists(runtimePath))
            {
                File.Copy(sourcePath, runtimePath);
            }

            db.Tasks.Add(new TaskModel
            {
                Name = Path.GetFileNameWithoutExtension(fileName),
                FileName = fileName,
                CreateTime = DateTime.Now,
                Enable = false,
            });
        }
    }
}
