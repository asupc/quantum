using Quantum.Entities.Model;

namespace Quantum.Entities.DTOs;

public class TaskCommandStep
{
    /// <summary>
    /// 线程id
    /// </summary>
    public string ThreadId { get; set; }

    /// <summary>
    /// 用户id
    /// </summary>
    public string UserId { get; set; }

    /// <summary>
    /// 是否还有子任务
    /// </summary>
    public bool HasChildTask { get; set; }

    /// <summary>
    /// 当前是否执行的子任务
    /// </summary>
    public bool IsChildTask { get; set; }

    /// <summary>
    /// 下一步任务
    /// </summary>
    public string NextSubTaskId { get; set; }

    /// <summary>
    /// 当前子任务ID
    /// </summary>
    public string CurrentSubTaskId { get; set; }

    /// <summary>
    /// 任务创建时间
    /// </summary>
    public DateTime CreateTime { get; set; }

    /// <summary>
    /// 更新时间
    /// </summary>
    public DateTime UpdateTime { get; set; }

    /// <summary>
    /// 强制结束任务时间
    /// </summary>
    public DateTime ForceEndTime { get; set; }

    public List<EnvModel> Envs { get; set; }

    public TaskModel Task { get; set; }

    /// <summary>
    /// 当前执行子任务名称
    /// </summary>
    public string SubTaskName { get; set; }
}
