namespace Quantum.Plugins;

/// <summary>环境变量门面：替代原脚本 serverAddres+api 的 env 环回调用。</summary>
public interface IQuantumEnv
{
    /// <summary>查询环境变量；name/key 均可空，空返回全部。</summary>
    Task<IReadOnlyList<QuantumEnvValue>> QueryAsync(string name = null, string key = null, CancellationToken ct = default);

    /// <summary>新增或更新环境变量（同名覆盖）。</summary>
    Task SaveAsync(string name, string value, string remark = null, bool enabled = true, CancellationToken ct = default);

    /// <summary>删除指定名称的环境变量。</summary>
    Task DeleteByNameAsync(string name, CancellationToken ct = default);

    /// <summary>启用/停用指定名称的环境变量。</summary>
    Task SetEnabledAsync(string name, bool enabled, CancellationToken ct = default);
}

/// <summary>环境变量条目。</summary>
public sealed record QuantumEnvValue(string Name, string Value, string Remark, bool Enabled, DateTime UpdateTime);

/// <summary>通知门面：替代原脚本 serverAddres+api 的 sendNotify 环回调用。</summary>
public interface IQuantumNotify
{
    /// <summary>发送文本通知（标题+内容）；是否实际送达由平台推送开关决定。</summary>
    Task SendAsync(string title, string content, CancellationToken ct = default);

    /// <summary>
    /// 发送图片消息：App 会话中以图片气泡展示，caption 为同气泡配文（可空）。
    /// imageUrl 传图片直链（外链 http/https 原样加载，或平台 AppUpload FileId 走鉴权下载）。
    /// options 非空时图片下挂可点选项块（封面卡片点选，如电影港结果订阅）。
    /// </summary>
    Task SendImageAsync(string imageUrl, string caption = null, IReadOnlyList<QuantumOption> options = null,
        CancellationToken ct = default);

    /// <summary>
    /// 发送视频消息：App 会话中以视频气泡展示（M1 起端内全屏播放），caption 为同气泡配文（可空）。
    /// videoUrl 传视频直链（外链 http/https 原样使用，或平台 AppUpload FileId 走鉴权下载）；
    /// posterUrl 为封面图地址（可空）：App 以封面作播放预览，点按才加载播放。
    /// </summary>
    Task SendVideoAsync(string videoUrl, string caption = null, string posterUrl = null, CancellationToken ct = default);

    /// <summary>
    /// 发送音频消息（2026-09-18 M2 新增）：App 会话中以音频气泡展示、端内播放器播放，
    /// caption 为同气泡配文（可空）。audioUrl 传音频直链（外链 http/https 原样使用）；
    /// 服务端媒体产物传相对地址「api/AppMedia/file?path=…」——App 按当前登录服务器地址
    /// 补全为绝对地址并带鉴权播放，服务端文件无直链时效问题。
    /// </summary>
    Task SendAudioAsync(string audioUrl, string caption = null, CancellationToken ct = default);

    /// <summary>
    /// 发送带可点选项的文本消息（2026-09-18 富交互批次）：App 在气泡下渲染选项块，
    /// 用户点按即以选项的 Reply（缺省 Key）作为指令文本回复——与手打完全等价的二次触发。
    /// options 至多 20 条；Color 取固定调色板 red/green/orange/blue/purple/gray（可空默认灰）。
    /// </summary>
    Task SendOptionsAsync(string content, IReadOnlyList<QuantumOption> options, CancellationToken ct = default);
}

/// <summary>
/// 可点选项（消息气泡下挂选项块的一条）：Key 为机器可读键（点选回复文本缺省用它），
/// Label 为展示文案，Desc 为可选副行说明，Color 为可选调色板色名。
/// </summary>
public sealed record QuantumOption(string Key, string Label, string Reply = null, string Color = null, string Desc = null);
