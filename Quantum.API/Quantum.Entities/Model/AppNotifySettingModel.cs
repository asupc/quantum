using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// App 通知偏好（全局单行，Id 固定 "global"）：三类厂商推送开关 + 免打扰时段。
/// 仅抑制厂商离线推送；落库与在线 WS 直推不受影响（打开 App 必可见）。
/// </summary>
[Table("t_app_notify_setting")]
public class AppNotifySettingModel
{
    /// <summary>
    /// 主键（固定 "global"，单管理员全局一行）
    /// </summary>
    public string Id { get; set; } = "global";

    /// <summary>
    /// 任务通知（category=task）
    /// </summary>
    public bool TaskPush { get; set; } = true;

    /// <summary>
    /// 系统通知（category=system）
    /// </summary>
    public bool SystemPush { get; set; } = true;

    /// <summary>
    /// 安全提醒（category=security）
    /// </summary>
    public bool SecurityPush { get; set; } = true;

    /// <summary>
    /// 免打扰开始（HH:mm，可空=不启用）
    /// </summary>
    public string DndStart { get; set; }

    /// <summary>
    /// 免打扰结束（HH:mm）
    /// </summary>
    public string DndEnd { get; set; }

    /// <summary>
    /// 更新时间
    /// </summary>
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
