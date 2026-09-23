using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// 外触内执实体
/// </summary>
[Table("t_opentriggertask")]
public class OpenTriggerTask : BaseModel
{
    [Required]
    public string Name { get; set; }

    [Required]
    public string Secret { get; set; }

    /// <summary>
    /// 执行脚本
    /// </summary>
    public string SrciptFile { get; set; }

    /// <summary>
    /// 请求方法
    /// </summary>
    public string HttpMethod { get; set; }

    /// <summary>
    /// 自定义环境变量
    /// </summary>
    public string Envs { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enable { get; set; }

    /// <summary>
    /// 白名单IP
    /// </summary>
    public string Whitelist { get; set; }

    /// <summary>
    /// 开启推送
    /// </summary>
    public bool EnablePush { get; set; }

    /// <summary>
    /// 开启代理
    /// </summary>
    public bool EnableProxy { get; set; }
}
