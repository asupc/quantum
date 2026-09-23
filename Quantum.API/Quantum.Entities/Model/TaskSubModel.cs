using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// 多步骤任务
/// </summary>
[Table("t_task_sub")]
public class TaskSubModel : BaseModel
{
    /// <summary>
    /// 步骤名称
    /// </summary>
    public string Name { get; set; }

    public bool EnableRegex { get; set; }

    /// <summary>
    /// 指令
    /// </summary>
    public string Command { get; set; }

    /// <summary>
    /// 多步骤任务排序
    /// </summary>
    public int Sort { get; set; }

    /// <summary>
    /// 主任务id
    /// </summary>
    public string TaskId { get; set; }

    /// <summary>
    /// 触发指令消息的环境变量名称
    /// </summary>
    public string CommandEnv { get; set; }


    /// <summary>
    /// 撤回群消息
    /// </summary>
    public bool Revocation { get; set; }

    /// <summary>
    /// 任务等待时间
    /// </summary>
    public int WaitTime { get; set; }

    /// <summary>
    /// 备注
    /// </summary>
    public string Remark { get; set; }
}
