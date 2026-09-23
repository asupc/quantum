using Quantum.Entities.Model;

namespace Quantum.Entities.DTOs;

/// <summary>
/// 任务新增/更新的入参模型：只暴露允许客户端写入的字段，
/// CreateTime / TaskStatus / TaskThreadId 等服务端管控字段不接收自请求体。
/// </summary>
public class TaskSaveModel
{
    public string Id { get; set; }

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

    /// <summary>
    /// 启用状态
    /// </summary>
    public bool Enable { get; set; }

    /// <summary>
    /// 执行限制（每日次数）
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

    /// <summary>
    /// 撤回群消息
    /// </summary>
    public bool Revocation { get; set; }

    /// <summary>
    /// 是否管理员指令
    /// </summary>
    public bool Manager { get; set; }

    /// <summary>
    /// 任务等待时间
    /// </summary>
    public int WaitTime { get; set; }

    /// <summary>
    /// 任务开始通知文本
    /// </summary>
    public string TaskStartNotify { get; set; }

    /// <summary>
    /// 任务执行完成通知文本
    /// </summary>
    public string TaskEndNotify { get; set; }

    public string Remark { get; set; }

    /// <summary>
    /// 通讯方式
    /// </summary>
    public string CommunicationTypes { get; set; }

    /// <summary>
    /// 开启代理
    /// </summary>
    public bool EnableProxy { get; set; }

    /// <summary>
    /// 会话名（可空）：多个任务填同一会话名即合并为一个客户端会话；空 = 不归组（1 任务 = 1 会话）
    /// </summary>
    public string SessionName { get; set; }

    /// <summary>
    /// 子任务
    /// </summary>
    public List<TaskSubSaveModel> TaskSubs { get; set; }
}

/// <summary>
/// 多步骤任务入参模型。
/// </summary>
public class TaskSubSaveModel
{
    public string Name { get; set; }

    public bool EnableRegex { get; set; }

    public string Command { get; set; }

    public int Sort { get; set; }

    public string CommandEnv { get; set; }

    public bool Revocation { get; set; }

    public int WaitTime { get; set; }

    public string Remark { get; set; }
}
