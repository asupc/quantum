namespace Quantum.Entities.DTOs;

/// <summary>
/// 首次部署初始化向导：状态查询返回（匿名端点 /api/Setup/status）。
/// 未初始化时给出当前库配置与自带脚本清单；已初始化后只回 NeedSetup=false（不外泄库配置/脚本清单）。
/// </summary>
public class SetupStatus
{
    /// <summary>是否需要走初始化向导（首次部署未初始化）。</summary>
    public bool NeedSetup { get; set; }

    /// <summary>当前生效的数据库类型（SQLite/MySql），仅未初始化时返回。</summary>
    public string DBType { get; set; }

    /// <summary>当前生效的数据库地址（SQLite 文件名 / MySQL 连接串），仅未初始化时返回。</summary>
    public string DBAddress { get; set; }

    /// <summary>随应用分发的自带脚本清单（向导第 3 步勾选用），仅未初始化时返回。</summary>
    public List<SetupScriptItem> Scripts { get; set; } = new();
}

/// <summary>自带脚本清单条目：文件名 + 用途说明（取脚本头部注释）。</summary>
public class SetupScriptItem
{
    /// <summary>脚本文件名（如 每日晨报.cs），建任务时的唯一标识。</summary>
    public string FileName { get; set; }

    /// <summary>任务名（文件名去扩展名）。</summary>
    public string Name { get; set; }

    /// <summary>用途说明（脚本头部注释首段，便于向导里一眼看懂是干嘛的）。</summary>
    public string Description { get; set; }
}

/// <summary>数据库试连请求（向导第 2 步「测试连接」）。</summary>
public class SetupDatabaseRequest
{
    public string DBType { get; set; }

    public string DBAddress { get; set; }
}

/// <summary>数据库试连结果。</summary>
public class SetupDatabaseTestResult
{
    public bool Success { get; set; }

    public string Message { get; set; }
}

/// <summary>初始化向导一次性提交：创建登录账号 + 选择数据库 + 勾选脚本建任务。</summary>
public class SetupCompleteRequest
{
    /// <summary>登录用户名（Web/App 共用唯一账号）。</summary>
    public string UserName { get; set; }

    /// <summary>登录密码（≥6 位）。</summary>
    public string PassWord { get; set; }

    /// <summary>数据库类型：SQLite / MySql（大小写不敏感）。</summary>
    public string DBType { get; set; }

    /// <summary>数据库地址：SQLite 文件名（落 db/ 内）或 MySQL 连接串。</summary>
    public string DBAddress { get; set; }

    /// <summary>勾选的自带脚本文件名；null/空 = 不建任何任务。</summary>
    public List<string> Scripts { get; set; }
}

/// <summary>初始化完成结果。</summary>
public class SetupCompleteResult
{
    /// <summary>数据库发生切换（如 SQLite→MySQL）：配置已保存，需重启服务后在新库生效。</summary>
    public bool RestartRequired { get; set; }

    /// <summary>本次创建的任务数（RestartRequired=true 时为 0，重启后自动创建）。</summary>
    public int TaskCount { get; set; }

    /// <summary>结果提示（前端向导完成页展示）。</summary>
    public string Message { get; set; }
}
