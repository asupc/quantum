using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

/// <summary>
/// 自定义环境变量配置
/// </summary>
[Table("t_custom_env_config")]
public class CustomEnvConfig : BaseModel
{
    /// <summary>
    /// 环境变量名称
    /// </summary>
    [Comment("环境变量名称")]
    public string EnvName { get; set; }

    /// <summary>
    /// 配置名称
    /// </summary>
    [Comment("配置名称")]
    public string Name { get; set; }

    /// <summary>
    /// 触发正则表达式
    /// </summary>
    [Comment("触发正则表达式")]
    public string TriggerRegex { get; set; }

    /// <summary>
    /// 更新环境变量取值表达式
    /// </summary>
    [Comment("更新环境变量取值表达式")]
    public string UpdateRegex { get; set; }

    /// <summary>
    /// 备注
    /// </summary>
    [Comment("备注")]
    public string Remark { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    [Comment("是否启用")]
    public bool Enable { get; set; }

    /// <summary>
    /// 白名单群号
    /// </summary>
    [Comment("白名单群号")]
    public string WhilteListGroup { get; set; }

    /// <summary>
    /// 提交/更新成功提示
    /// </summary>
    [Comment("提交/更新成功提示")]
    public string SuccessTips { get; set; }

    /// <summary>
    /// 提交失败的提示信息
    /// </summary>
    [Comment("提交失败的提示信息")]
    public string FailedTips { get; set; }

    /// <summary>
    /// 分隔符
    /// </summary>
    [Comment("环境变量多个值分隔符")]
    public string SplitChar { get; set; }

    /// <summary>
    /// 积分扣除
    /// </summary>
    [Comment("积分扣除")]
    public int Score { get; set; }

}

/// <summary>
/// 环境变量配置取值正则表达式
/// </summary>
[Table("t_custom_env_config_regex")]
[Comment("环境变量配置取值正则表达式")]
public class CustomEnvConfigRegex : BaseModel
{
    [Comment("自定义环境变量配置Id")]
    public string CustomEnvConfigId { get; set; }

    /// <summary>
    /// Key
    /// </summary>
    [Comment("取值后赋值Key")]
    [Required]
    public string Key { get; set; }

    /// <summary>
    /// 取值正则表达式
    /// </summary>
    [Comment("取值正则表达式")]
    [Required]
    public string ValueRegex { get; set; }

    /// <summary>
    /// 是否必要
    /// </summary>
    [Comment("是否必要")]
    public bool IsRequired { get; set; }
}
