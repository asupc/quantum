using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MySql.EntityFrameworkCore.Extensions;
using Quantum.Entities.Model;
using Quantum.Utils;

namespace Quantum.Data;

public class QuantumMySqlDbContext : DbContext, IQuantumDbContext
{
    public QuantumMySqlDbContext(DbContextOptions<QuantumMySqlDbContext> options) : base(options)
    {
    }

    public QuantumMySqlDbContext()
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        QuantumModelConfiguration.Configure(modelBuilder);

        // MySQL 列型显式收紧（参考 AddChatSessionView 对消息表 SessionKey 的收紧做法，
        // 改为模型层配置使后续自动生成的迁移不会再回退为 longtext）：
        // t_task.SessionName varchar(100)（入站会话键即会话名原文，100 留足余量）；
        // t_chat_session.SessionKey varchar(255)（主键要求可索引类型，对齐消息表 SessionKey 列）。
        modelBuilder.Entity<TaskModel>().Property(n => n.SessionName).HasColumnType("varchar(100)");
        modelBuilder.Entity<ChatSessionModel>().Property(n => n.SessionKey).HasColumnType("varchar(255)");

        base.OnModelCreating(modelBuilder);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var setting = SystemConfigHelper.GetSetting();
            optionsBuilder.UseMySQL(NormalizeCharsetToUtf8mb4(setting.DBAddress));
        }
    }

    /// <summary>
    /// MySQL 连接串字符集统一 utf8mb4（2026-09-20 生产 AI 修复流程事故根因）：
    /// 历史连接串写死 CharSet=utf8（= utf8mb3，最多 3 字节），任何含 4 字节字符
    /// （脚本里的 emoji、新闻标题等）的内容插入即报 1366 Incorrect string value，
    /// Oracle MySQL EF 提供方还会把它包成「Could not save changes. Please configure
    /// your entity type accordingly.」吞掉真实原因——AI 步骤日志回填含 emoji 的脚本
    /// 全文时必然踩中。所有取连接串建库的落点统一经本方法归一：已有 utf8/utf8mb3
    /// 升级为 utf8mb4；缺失则追加（utf8mb4 是 utf8mb3 的超集，对存量数据无影响）。
    /// </summary>
    public static string NormalizeCharsetToUtf8mb4(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return address;
        }
        if (Regex.IsMatch(address, @"CharSet\s*=", RegexOptions.IgnoreCase))
        {
            return Regex.Replace(address, @"(CharSet\s*=\s*)utf8(mb3)?\b", "$1utf8mb4", RegexOptions.IgnoreCase);
        }
        return address.TrimEnd(';') + ";CharSet=utf8mb4";
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
