using Microsoft.EntityFrameworkCore;
using Quantum.Data;
using Quantum.Entities.Model;

using Quantum.Utils;
using System.Text.RegularExpressions;
namespace Quantum.Application;

/// <summary>
/// App 通知偏好（全局单行，Id 固定 "global"）：GET/PUT api/App/notify-setting。
/// 免打扰时段与分类开关由 **App 端**读取后抑制本地通知弹出（厂商离线推送已移除，
/// 服务端不再自行判断）；落库与在线 WS 直推不受影响（打开 App 必可见）。
/// </summary>
public class AppNotifySettingService
{
    /// <summary>
    /// 全局单行主键
    /// </summary>
    public const string GlobalId = "global";

    /// <summary>
    /// 免打扰时段合法形态：00:00-23:59 的 HH:mm。
    /// 旧校验只查 "\d{2}:\d{2}" 形态，25:99 也能落库 → 客户端解析失败后静默不抑制。
    /// </summary>
    private static readonly Regex DndTimeRegex = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

    private readonly IQuantumDbContext _dbContext;

    public AppNotifySettingService(IQuantumDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AppNotifySettingModel> GetAsync()
    {
        var setting = await _dbContext.AppNotifySettings.AsNoTracking()
            .SingleOrDefaultAsync(n => n.Id == GlobalId);
        return setting ?? new AppNotifySettingModel { Id = GlobalId };
    }

    public async Task<AppNotifySettingModel> UpdateAsync(AppNotifySettingModel input)
    {
        if (!IsValidDndTime(input.DndStart))
        {
            throw new BusinessException("免打扰开始时间应为 HH:mm（00:00-23:59）！");
        }
        if (!IsValidDndTime(input.DndEnd))
        {
            throw new BusinessException("免打扰结束时间应为 HH:mm（00:00-23:59）！");
        }
        var setting = await _dbContext.AppNotifySettings.SingleOrDefaultAsync(n => n.Id == GlobalId);
        if (setting == null)
        {
            setting = new AppNotifySettingModel { Id = GlobalId };
            _dbContext.AppNotifySettings.Add(setting);
        }
        setting.TaskPush = input.TaskPush;
        setting.SystemPush = input.SystemPush;
        setting.SecurityPush = input.SecurityPush;
        setting.DndStart = input.DndStart;
        setting.DndEnd = input.DndEnd;
        setting.UpdateTime = DateTime.Now;
        await _dbContext.SaveChangesAsync();
        return setting;
    }

    /// <summary>
    /// 免打扰时段校验：空白=不启用（允许清空），非空必须是 00:00-23:59 的合法 HH:mm。
    /// </summary>
    private static bool IsValidDndTime(string value) =>
        string.IsNullOrWhiteSpace(value) || DndTimeRegex.IsMatch(value.Trim());
}
