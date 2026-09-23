namespace Quantum.Entities.Config;

/// <summary>
/// 系统配置：持久化于 appsettings.json 的 Quantum 节（SystemConfigHelper 读写）。
/// 敏感字段不在此处做序列化排除——管理端响应由 SystemConfigService 手工脱敏，
/// 配置文件写回需要完整往返（含密钥），删除会导致密钥丢失。
/// </summary>
public class Setting
{
    /// <summary>
    /// 用户名
    /// </summary>
    public string UserName { get; set; }

    /// <summary>
    /// 密码
    /// </summary>
    public string PassWord { get; set; }

    /// <summary>
    /// 数据库类型
    /// </summary>
    public string DBType { get; set; }

    /// <summary>
    /// 数据库地址
    /// </summary>
    public string DBAddress { get; set; }

    /// <summary>
    /// 端口号
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// 绑定host
    /// </summary>
    public string Host { get; set; }

    /// <summary>
    /// 黑名单QQ
    /// </summary>
    public string BlackQQ { get; set; }

    public string AppKey { get; set; }


    /// <summary>
    /// 指令响应时间间隔单位（s）
    /// </summary>
    public int CommandTimeInterval { get; set; }

    public int MessageQueueInterval { get; set; } = 100;

    /// <summary>
    /// 服务地址
    /// </summary>
    public string ServerPath { get; set; }

    /// <summary>
    /// 积分比例
    /// </summary>
    public int IntegralProportion { get; set; }

    /// <summary>
    /// 消息队列间隔时间
    /// </summary>
    public int MessageInterval { get; set; }

    /// <summary>
    /// 进程内脚本执行并发上限（§1-2）：无上限时批量触发会打爆宿主线程/DI/HttpClient。
    /// 仅在首次用到执行闸时读取一次（改值需重启生效），非正值由配置容错收敛为 8。
    /// </summary>
    public int MaxConcurrentTasks { get; set; } = 8;

    /// <summary>
    /// 量子登录提醒开关
    /// </summary>
    public bool LoginNotify { get; set; }

    /// <summary>
    /// 自定义页脚
    /// </summary>
    public string Footer { get; set; }

    public string SymmetricSecurityKey { get; set; }

    public string SecurityAudience { get; set; }

    public string SecurityIssuer { get; set; }

    /// <summary>
    /// 可信反向代理列表（逗号分隔，支持 IP 或 CIDR，如 "172.17.0.1,172.16.0.0/12"）：
    /// 仅这些直连方的 X-Forwarded-For/X-Forwarded-Proto 会被采信（登录限流、白名单取 IP 的依据）。
    /// 留空 = 不信任任何 XFF（公网直连部署的默认安全态）。
    /// </summary>
    public string KnownProxies { get; set; }

    /// <summary>
    /// 允许跨域调用 API 的来源列表（逗号分隔 Origin，如 "https://admin.example.com"）。
    /// 留空 = 拒绝一切跨域（前端与管理端同源部署时无需配置；本机开发例外见 Startup）。
    /// </summary>
    public string AllowedOrigins { get; set; }

    /// <summary>
    /// 是否在生产环境暴露 Swagger（默认 false）。公网部署保持关闭。
    /// </summary>
    public bool EnableSwagger { get; set; }

    /// <summary>
    /// 管理令牌签发下限（Unix 秒）：修改管理员密码时置为当前时间，早于该时刻签发的
    /// Manager 令牌（Web 7 天/App 2 小时）全部作废，作为无状态令牌的吊销语义。
    /// </summary>
    public long ManagerTokenNotBefore { get; set; }

    /// <summary>
    /// 任务脚本产物下载根目录（ctx.File 门面可写范围的根）：绝对路径或相对运行目录的相对路径，
    /// 留空 = downloads。脚本侧子目录（如音乐脚本 scripts_music_save_dir）只能落在此根内，
    /// 脚本门禁仍禁 System.IO，产物落盘只有该门面一条受控通道。
    /// </summary>
    public string FileDownloadRoot { get; set; }

    /// <summary>
    /// App 刷新令牌有效天数（滑动续期：每次刷新发全新窗口）。留空/0 = 默认 365，
    /// 服务端 clamp 到 [7, 365]——上限保护防止「永不过期」的不可逆选择。
    /// </summary>
    public int RefreshTokenDays { get; set; }

    /// <summary>
    /// 浅拷贝：全部属性为标量或不可变 string，MemberwiseClone 即完全隔离。
    /// 供 SystemConfigHelper.GetSetting 在 memoize 后向调用方返回独立副本——若干调用方
    /// （脱敏展示、改密/改设置）会就地修改返回对象，不能污染缓存中的权威实例。
    /// </summary>
    public Setting Clone() => (Setting)MemberwiseClone();
}
