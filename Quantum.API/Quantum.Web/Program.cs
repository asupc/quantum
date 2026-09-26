using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quantum.Utils;

namespace Quantum.Web;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}：量子启动，当前版本号：" + Extends.Version);
        // scripts/quantum 是脚本保存/上传/AI 影子跑批的落盘根目录，只由运行期投放、不随发布产物分发，
        // 新实例缺它时首个写入直接抛 DirectoryNotFoundException（500 且堆栈回进响应）
        var dirs = new List<string> { "logs", "config", "db", Path.Combine("scripts", "quantum") };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        var config = SystemConfigHelper.GetSetting();
        var hostBuilder = Host.CreateDefaultBuilder(args)
        .UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = false;
        })
        .UseServiceProviderFactory(new AutofacServiceProviderFactory())
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>()
                .UseUrls(config.Host + ":" + config.Port);
            });
        return hostBuilder;
    }
}