using System.ComponentModel.DataAnnotations.Schema;

namespace Quantum.Entities.Model;

[Table("t_command")]
public class CommandModel : BaseModel
{
    /// <summary>
    /// 触发指令
    /// </summary>
    public string Key { get; set; }

    /// <summary>
    /// 消息内容
    /// </summary>
    public string Message { get; set; }

    /// <summary>
    /// 指定消息的通讯类型，不指定则表示全部
    /// </summary>
    public CommunicationType? CommunicationType { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>

    public bool Enable { get; set; }

    /// <summary>
    /// 开启正则匹配
    /// </summary>
    public bool EnableRegex { get; set; }

    /// <summary>
    /// 消息类型
    /// </summary>
    public MessageType MessageType { get; set; } = MessageType.文本;

    /// <summary>
    /// 备注
    /// </summary>

    public string Remark { get; set; }
}
