using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

[Table("t_task")]
public class TaskModel : BaseModel
{

    /// <summary>
    /// 任务名称
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// 触发指令
    /// </summary>
    public string Command { get; set; }

    /// <summary>
    /// 触发指令消息的环境变量名称
    /// 如赋值message，当用户发送的消息这个指令时，量子助手将额外提供一个 message = "用户发送消息";
    /// </summary>
    public string CommandEnv { get; set; }

    /// <summary>
    /// 定时执行
    /// </summary>
    public string Cron { get; set; }

    /// <summary>
    /// 脚本文件
    /// </summary>
    public string FileName { get; set; }

    /// <summary>
    /// 文字转图片
    /// </summary>
    public bool TextToPicture { get; set; }

    /// <summary>
    /// 开启正则匹配
    /// </summary>
    public bool EnableRegex { get; set; }

    ///// <summary>
    ///// 脚本缓存
    ///// </summary>
    //public bool FileCache { get; set; }

    ///// <summary>
    ///// 是否并发
    ///// </summary>
    //[Obsolete("弃用字段")]
    //public bool EnableConc { get; set; }

    /// <summary>
    /// 启用状态
    /// </summary>
    public bool Enable { get; set; }

    /// <summary>
    /// 执行限制
    /// </summary>
    public int DayLimit { get; set; }

    /// <summary>
    /// 开启推送
    /// </summary>
    public bool EnablePush { get; set; }

    /// <summary>
    /// 允许群消息通知
    /// </summary>
    public bool PushGroup { get; set; }

    ///// <summary>
    ///// 启用日志
    ///// </summary>
    //public bool EnableLog { get; set; }

    /// <summary>
    /// 撤回群消息
    /// </summary>
    public bool Revocation { get; set; }

    ///// <summary>
    ///// 并发环境变量名称
    ///// </summary>
    //public string ConcEnvName { get; set; }

    /// <summary>
    /// 是否管理员指令（管理员指令将只能管理员触发，且消息只通知管理员）
    /// </summary>
    public bool Manager { get; set; }

    /// <summary>
    /// 任务等待时间
    /// </summary>
    public int WaitTime { get; set; }

    public DateTime CreateTime { get; set; }

    /// <summary>
    /// 任务状态
    /// </summary>
    public TaskStatus TaskStatus { get; set; }

    /// <summary>
    /// 任务线程id
    /// </summary>
    public string TaskThreadId { get; set; }

    /// <summary>
    /// 任务开始通知文本
    /// 如果开启任务并发时指定，
    /// 如果不开启并发则可以在任务脚本中通知
    /// </summary>
    public string TaskStartNotify { get; set; }

    /// <summary>
    /// 任务执行完成通知文本
    /// </summary>
    public string TaskEndNotify { get; set; }

    public string Remark { get; set; }

    /// <summary>
    /// 子任务
    /// </summary>
    [NotMapped]
    public virtual IEnumerable<TaskSubModel> TaskSubs { get; set; }

    /// <summary>
    /// 通讯方式
    /// </summary>
    public string CommunicationTypes { get; set; }

    /// <summary>
    /// 开启代理
    /// </summary>
    public bool EnableProxy { get; set; }

    /// <summary>
    /// 会话名（可空）。多个任务填同一会话名时，出站消息合并进同一客户端会话；
    /// 空 = 不归组，出站会话键仍为任务 Id（原行为）。
    /// </summary>
    public string SessionName { get; set; }

}
