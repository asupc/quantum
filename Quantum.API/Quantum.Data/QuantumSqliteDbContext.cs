using Microsoft.EntityFrameworkCore;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Data;

public class QuantumSqliteDbContext : DbContext, IQuantumDbContext
{
    public QuantumSqliteDbContext(DbContextOptions<QuantumSqliteDbContext> options) : base(options)
    {
    }

    public QuantumSqliteDbContext()
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        QuantumModelConfiguration.Configure(modelBuilder);

        base.OnModelCreating(modelBuilder);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var setting = SystemConfigHelper.GetSetting();
            var address = "Filename=db/" + setting.DBAddress;
            optionsBuilder.UseSqlite(address);
        }
    }

    public DbSet<CommandModel> Commands { get; set; }

    public DbSet<EnvModel> Envs { get; set; }

    public DbSet<LogModel> Logs { get; set; }

    public DbSet<TaskModel> Tasks { get; set; }

    public DbSet<TaskSubModel> TaskSubs { get; set; }

    public DbSet<CustomDataModel> CustomDatas { get; set; }

    public DbSet<CustomDataTitleModel> CustomDataTitles { get; set; }


    public DbSet<OpenTriggerTask> OpenTriggerTasks { get; set; }

    public DbSet<Bookmark> Bookmarks { get; set; }
    public DbSet<MenuModel> Menus { get; set; }

    public DbSet<AppDeviceModel> AppDevices { get; set; }

    public DbSet<AppRefreshTokenModel> AppRefreshTokens { get; set; }

    public DbSet<AppNotificationModel> AppNotifications { get; set; }

    public DbSet<ChatMessageModel> ChatMessages { get; set; }

    public DbSet<ChatSessionModel> ChatSessions { get; set; }

    public DbSet<AppFileModel> AppFiles { get; set; }

    public DbSet<AppNotifySettingModel> AppNotifySettings { get; set; }

    public DbSet<ScriptVersionModel> ScriptVersions { get; set; }

    public DbSet<AiProviderModel> AiProviders { get; set; }

    public DbSet<AiModelModel> AiModels { get; set; }

    public DbSet<AiSettingModel> AiSettings { get; set; }

    public DbSet<AiConversationModel> AiConversations { get; set; }

    public DbSet<AiMessageModel> AiMessages { get; set; }

    public DbSet<AiRunModel> AiRuns { get; set; }

    public DbSet<AiStepModel> AiSteps { get; set; }

    public DbSet<AiProposalModel> AiProposals { get; set; }
}
