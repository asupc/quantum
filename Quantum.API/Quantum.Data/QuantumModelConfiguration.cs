using Microsoft.EntityFrameworkCore;
using Quantum.Entities.Model;

namespace Quantum.Data;

/// <summary>
/// 两个 DbContext（SQLite/MySQL）共享的模型配置。
/// 说明：仅添加常用查询索引（存量数据无需迁移即可在新库生效；存量库的索引随迁移体系重建时统一补齐）。
/// </summary>
public static class QuantumModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        // 主子关系：任务子步骤按主任务查询
        modelBuilder.Entity<TaskSubModel>().HasIndex(n => n.TaskId);

        // 自定义数据按类型查询
        modelBuilder.Entity<CustomDataModel>().HasIndex(n => n.Type);

        // 日志按时间排序/过滤
        modelBuilder.Entity<LogModel>().HasIndex(n => n.CreateTime);

        // 外触内执按 secret 定位
        modelBuilder.Entity<OpenTriggerTask>().HasIndex(n => n.Secret);

        // 会话消息按 Seq 取增量游标（全局单调递增）。
        // 唯一约束是 AppendAsync「事务内 MAX+1 + 撞唯一键重试」并发策略的前提——没有它，
        // 并发插入双双成功且 Seq 相同（重试是死循环），重复 Seq 会破坏增量同步与已读回执语义。
        modelBuilder.Entity<ChatMessageModel>().HasIndex(n => n.Seq).IsUnique();

        // 会话视图按「会话键 + Seq」检索：会话列表聚合（每组 Max/Count）、单会话倒序分页与
        // 未读计数都落在这个复合索引上（默认会话的 SessionKey 为 null，同样命中）。
        modelBuilder.Entity<ChatMessageModel>().HasIndex(n => new { n.SessionKey, n.Seq });

        // 站内通知按时间拉取
        modelBuilder.Entity<AppNotificationModel>().HasIndex(n => n.CreatedAt);

        // App 上传文件按时间排序
        modelBuilder.Entity<AppFileModel>().HasIndex(n => n.CreateTime);

        // 通知偏好：全局单行（Id 固定 "global"）
        modelBuilder.Entity<AppNotifySettingModel>().HasKey(n => n.Id);

        // 脚本版本按「文件 + 时间」检索（版本列表分页、最新版本去重、保留数清理都落在这个复合索引上）
        modelBuilder.Entity<ScriptVersionModel>().HasIndex(n => new { n.FileName, n.CreateTime });

        // AI 模型按供应商检索（列表按供应商聚合、默认模型/启用模型查询）
        modelBuilder.Entity<AiModelModel>().HasIndex(n => n.ProviderId);

        // AI 全局设置：单行（Id 固定 "default"）
        modelBuilder.Entity<AiSettingModel>().HasKey(n => n.Id);

        // Agent 消息按「会话 + 序号」增量拉取（前端 afterSeq 轮询）
        modelBuilder.Entity<AiMessageModel>().HasIndex(n => new { n.ConversationId, n.Seq });

        // Agent 运行轨迹按运行 Id 顺序取
        modelBuilder.Entity<AiStepModel>().HasIndex(n => new { n.RunId, n.Seq });

        // 待确认提案按状态筛选（应用/过期清理）
        modelBuilder.Entity<AiProposalModel>().HasIndex(n => n.Status);

        // 运行记录按「任务 + 受理时间」分页（执行历史列表主查询），以及按根执行取整条重试链
        modelBuilder.Entity<TaskRunModel>().HasIndex(n => new { n.TaskId, n.CreatedAtUtc });
        modelBuilder.Entity<TaskRunModel>().HasIndex(n => new { n.RootRunId, n.Attempt }).IsUnique();
        // 到期重试领取与启动中断扫描都走这个复合索引（Status + NextAttemptAtUtc）
        modelBuilder.Entity<TaskRunModel>().HasIndex(n => new { n.Status, n.NextAttemptAtUtc });
        modelBuilder.Entity<TaskRunModel>().HasIndex(n => n.LogId);

        // 失败策略一任务一条：唯一键是 Upsert 并发的前提
        modelBuilder.Entity<TaskFailurePolicyModel>().HasIndex(n => n.TaskId).IsUnique();

        // 告警状态一任务一条
        modelBuilder.Entity<TaskAlertStateModel>().HasIndex(n => n.TaskId).IsUnique();

        // 告警事件按「任务 + 类型 + 根执行」去重（同一根执行不得重复开/恢复），投递扫描按状态取
        modelBuilder.Entity<TaskAlertEventModel>().HasIndex(n => new { n.TaskId, n.AlertType, n.RootRunId }).IsUnique();
        modelBuilder.Entity<TaskAlertEventModel>().HasIndex(n => new { n.DeliveryStatus, n.CreatedAtUtc });

        // 外部推送：幂等键按「凭据 + 键」唯一（同键并发只有一个能提交，冲突后回查去重）
        modelBuilder.Entity<ExternalPushRequestModel>()
            .HasIndex(n => new { n.CredentialId, n.IdempotencyKey }).IsUnique();
        // 配额计数与过期清理都按「凭据 + 时间」扫描
        modelBuilder.Entity<ExternalPushRequestModel>().HasIndex(n => new { n.CredentialId, n.CreatedAtUtc });

        // 外部推送凭据按启用态检索（列表/校验路径）
        modelBuilder.Entity<ExternalPushCredentialModel>().HasIndex(n => n.Enabled);
    }
}
