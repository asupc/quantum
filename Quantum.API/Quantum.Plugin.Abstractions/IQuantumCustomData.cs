namespace Quantum.Plugins;

/// <summary>
/// 自定义数据门面：替代原脚本 serverAddres+api 的 CustomData 环回调用。
/// Data1-Data5 为常用列（原量子密码等脚本语义）；Data6-Data15 需经 <see cref="QuantumCustomDataFilter"/> 查询
/// （与平台数据管理页一致：条件为包含匹配、条件间为与关系，查询结果固定按创建时间倒序）。
/// </summary>
public interface IQuantumCustomData
{
    /// <summary>按类型查询；Data1-Data5 任一非空即作包含匹配过滤，条件间为与关系。</summary>
    Task<IReadOnlyList<QuantumCustomDataValue>> QueryAsync(string type, string data1 = null, string data2 = null,
        string data3 = null, string data4 = null, string data5 = null, CancellationToken ct = default);

    /// <summary>按类型 + 完整过滤条件（Data1-Data15、创建时间范围）查询。</summary>
    Task<IReadOnlyList<QuantumCustomDataValue>> QueryAsync(string type, QuantumCustomDataFilter filter,
        CancellationToken ct = default);

    /// <summary>批量新增（Type 必填；Id 与时间由平台生成）。</summary>
    Task AddAsync(IReadOnlyList<QuantumCustomDataValue> items, CancellationToken ct = default);

    /// <summary>
    /// 按 Id 批量更新（须携带查询结果中的 Id 与 Type，未携带的列会被清空，建议先查后改）。
    /// 注意：若目标行是本次执行里 AddAsync 刚写入的，其实例仍被 DbContext 跟踪，更新会报主键冲突——
    /// 请在新增时直接写对，或拆成两次任务执行。
    /// </summary>
    Task UpdateAsync(IReadOnlyList<QuantumCustomDataValue> items, CancellationToken ct = default);

    /// <summary>按 Id 批量删除。</summary>
    Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct = default);

    /// <summary>
    /// 新增或更新数据类型表头（titles 依次对应 Data1-Data15，缺省列留空；
    /// 同步刷新数据管理页左侧菜单名，菜单不存在则不创建）。
    /// 注意：与 <see cref="UpdateAsync"/> 同源——同一次执行内对同一 type 二次写入会因行实例仍被跟踪而报主键冲突。
    /// </summary>
    Task SaveTitleAsync(string type, string typeName, string[] titles, CancellationToken ct = default);
}

/// <summary>
/// 自定义数据查询过滤条件：任一列非空即作包含匹配过滤（与数据管理页一致），
/// 全部为空则返回该类型全部数据；创建时间范围为闭区间，null 表示不限。
/// </summary>
public sealed record QuantumCustomDataFilter
{
    public string Data1 { get; init; }
    public string Data2 { get; init; }
    public string Data3 { get; init; }
    public string Data4 { get; init; }
    public string Data5 { get; init; }
    public string Data6 { get; init; }
    public string Data7 { get; init; }
    public string Data8 { get; init; }
    public string Data9 { get; init; }
    public string Data10 { get; init; }
    public string Data11 { get; init; }
    public string Data12 { get; init; }
    public string Data13 { get; init; }
    public string Data14 { get; init; }
    public string Data15 { get; init; }

    /// <summary>创建时间下界（含）。</summary>
    public DateTime? CreateTimeStart { get; init; }

    /// <summary>创建时间上界（含）。</summary>
    public DateTime? CreateTimeEnd { get; init; }
}

/// <summary>自定义数据条目（查询结果与新增/更新入参共用；Data1-Data15 全列暴露）。</summary>
public sealed record QuantumCustomDataValue
{
    /// <summary>条目 Id（查询结果携带；新增时由平台生成，UpdateAsync/DeleteAsync 按此定位）。</summary>
    public string Id { get; init; }

    /// <summary>数据类型标识（如 quantum_password）。</summary>
    public string Type { get; init; }

    public string Data1 { get; init; }
    public string Data2 { get; init; }
    public string Data3 { get; init; }
    public string Data4 { get; init; }
    public string Data5 { get; init; }
    public string Data6 { get; init; }
    public string Data7 { get; init; }
    public string Data8 { get; init; }
    public string Data9 { get; init; }
    public string Data10 { get; init; }
    public string Data11 { get; init; }
    public string Data12 { get; init; }
    public string Data13 { get; init; }
    public string Data14 { get; init; }
    public string Data15 { get; init; }

    /// <summary>创建时间（新增入参时无需填写）。</summary>
    public DateTime CreateTime { get; init; }

    /// <summary>更新时间（新增入参时无需填写）。</summary>
    public DateTime? UpdateTime { get; init; }
}
