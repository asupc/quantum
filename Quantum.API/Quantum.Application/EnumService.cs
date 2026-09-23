using System.Reflection;

namespace Quantum.Application;

public class EnumService
{
    static readonly Lazy<Dictionary<string, List<EnumKeyValue>>> EnumsCache = new(BuildEnums);

    /// <summary>
    /// 获取所有枚举信息（程序集枚举反射结果进程内缓存，避免每次请求重复加载程序集）
    /// </summary>
    /// <returns></returns>
    public Dictionary<string, List<EnumKeyValue>> Enums()
    {
        return EnumsCache.Value;
    }

    static Dictionary<string, List<EnumKeyValue>> BuildEnums()
    {
        Dictionary<string, List<EnumKeyValue>> result = new();
        // 多项目分层后枚举分布在 Quantum.* 各程序集（主要在 Entities）：
        // 扫描输出目录全部 Quantum*.dll 并按类型名去重，避免硬编码单一程序集造成漏采
        // （曾硬编码 Quantum.dll，分层后主程序集已无枚举，接口返回空导致前端枚举列渲染失败）
        foreach (var dll in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Quantum*.dll"))
        {
            Type[] types;
            try
            {
                types = Assembly.LoadFile(dll).GetTypes();
            }
            catch (Exception)
            {
                // 加载/反射失败的程序集跳过（其余程序集仍可贡献枚举）
                continue;
            }
            foreach (var type in types.Where(n => n.BaseType != null && n.BaseType.FullName == "System.Enum"))
            {
                if (result.ContainsKey(type.Name))
                {
                    continue;
                }
                var t = Enum.GetValues(type);
                List<EnumKeyValue> enums = [];
                for (var i = 0; i < t.Length; i++)
                {
                    var e = t.GetValue(i) as Enum;
                    enums.Add(new EnumKeyValue
                    {
                        Key = e.ToString(),
                        Value = Convert.ToInt32(e)
                    });
                }
                result.Add(type.Name, enums);
            }
        }
        return result;
    }
}

/// <summary>
/// 枚举对象Key-Value
/// </summary>
public class EnumKeyValue
{
    /// <summary>
    /// 显示字符串
    /// </summary>
    public string Key { get; set; }

    /// <summary>
    /// 枚举值
    /// </summary>
    public int Value { get; set; }
}
