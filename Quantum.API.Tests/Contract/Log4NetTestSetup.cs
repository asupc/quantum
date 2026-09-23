using log4net.Core;

namespace Quantum.API.Tests.Contract;

/// <summary>
/// 测试进程内初始化 log4net 仓库。
/// 生产代码里 ExceptionFilter/CustomAuthorizationFilter 等通过
/// LogManager.GetLogger("NETCoreRepository", ...) 取 logger，该仓库由 Startup 创建；
/// 单测不经过 Startup，这里幂等补建。
/// </summary>
public static class Log4NetTestSetup
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureRepository()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }
            try
            {
                log4net.LogManager.CreateRepository("NETCoreRepository");
            }
            catch (LogException)
            {
                // 仓库已存在（同进程重复初始化）
            }
            _initialized = true;
        }
    }
}
