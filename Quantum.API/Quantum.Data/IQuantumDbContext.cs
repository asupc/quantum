using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Quantum.Entities.Model;

namespace Quantum.Data;

public interface IQuantumDbContext
{
    DbSet<CommandModel> Commands { get; set; }
    DbSet<EnvModel> Envs { get; set; }
    DbSet<LogModel> Logs { get; set; }
    DbSet<TaskModel> Tasks { get; set; }
    DbSet<TaskSubModel> TaskSubs { get; set; }
    DbSet<CustomDataModel> CustomDatas { get; set; }
    DbSet<CustomDataTitleModel> CustomDataTitles { get; set; }
    DbSet<OpenTriggerTask> OpenTriggerTasks { get; set; }
    DbSet<Bookmark> Bookmarks { get; set; }
    DbSet<MenuModel> Menus { get; set; }
    DbSet<AppDeviceModel> AppDevices { get; set; }
    DbSet<AppRefreshTokenModel> AppRefreshTokens { get; set; }
    DbSet<AppNotificationModel> AppNotifications { get; set; }
    DbSet<ChatMessageModel> ChatMessages { get; set; }
    DbSet<ChatSessionModel> ChatSessions { get; set; }
    DbSet<AppFileModel> AppFiles { get; set; }
    DbSet<AppNotifySettingModel> AppNotifySettings { get; set; }
    DbSet<ScriptVersionModel> ScriptVersions { get; set; }
    DbSet<AiProviderModel> AiProviders { get; set; }
    DbSet<AiModelModel> AiModels { get; set; }
    DbSet<AiSettingModel> AiSettings { get; set; }
    DbSet<AiConversationModel> AiConversations { get; set; }
    DbSet<AiMessageModel> AiMessages { get; set; }
    DbSet<AiRunModel> AiRuns { get; set; }
    DbSet<AiStepModel> AiSteps { get; set; }
    DbSet<AiProposalModel> AiProposals { get; set; }

    DatabaseFacade Database { get; }
    EntityEntry Entry(object entity);
    EntityEntry<TEntity> Update<TEntity>(TEntity entity) where TEntity : class;

    /// <summary>变更跟踪器直通（AI 写工具按域解除残留跟踪用，见 AgentWriteTools.DetachFor）。</summary>
    ChangeTracker ChangeTracker { get; }

    /// <summary>
    /// 泛型实体集访问（DbContext.Set 的直通），供 QuantumDbContextExtensions 提供统一的
    /// GetById/Add/Update/Delete 辅助，是全项目除 DbSet 属性外的唯一通用数据入口。
    /// </summary>
    DbSet<T> Set<T>() where T : class;

    int SaveChanges();
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
