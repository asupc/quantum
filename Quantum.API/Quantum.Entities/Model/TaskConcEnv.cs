using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;


/// <summary>
/// 任务并发环境变量
/// </summary>
[Table("t_task_conc_env")]
public class TaskConcEnv : BaseModel
{
    /// <summary>
    /// 任务id
    /// </summary>
    public string TaskId { get; set; }

    /// <summary>
    /// 环境变量id
    /// </summary>
    public string EnvId { get; set; }
}
