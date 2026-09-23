package com.quantum.app.core.storage.db

import androidx.room.Database
import androidx.room.RoomDatabase

@Database(
    entities = [
        ChatMessageEntity::class,
        ChatSessionEntity::class,
        NotificationEntity::class,
        SyncCursorEntity::class,
        OutboxEntity::class
    ],
    // v2：chat_message 增加配文列 contentText；v3：增加会话键列 sessionId（按脚本分会话）；
    // v4：增加富交互列 payload/chosenKey（可点选项/封面）；
    // v5：新增会话表 chat_session（会话独立存在性：清空保留/单独删除，列表改会话表为主）。
    // v6：chat_message 加二级索引（sessionId+seq 联合 / msgId 非唯一）、notification 加 msgId 索引
    //（未读聚合一与按 msgId 按需查元数据命中索引）。
    // v7：富交互已选态多值化——chosenKey 单值列废弃，新增 pickedKeys（JSON 数组，多值「已选」）
    //与 pickLabel（点选代发行渲染为居中系统提示；均为本端单端记忆，见 docs/选项已选态改造计划.md）。
    // 本地库是可重建缓存（fallbackToDestructiveMigration 清库后由服务端全量补拉重建），不做逐版本迁移
    version = 7,
    exportSchema = false
)
abstract class QuantumDatabase : RoomDatabase() {
    abstract fun chatMessageDao(): ChatMessageDao
    abstract fun chatSessionDao(): ChatSessionDao
    abstract fun notificationDao(): NotificationDao
    abstract fun syncCursorDao(): SyncCursorDao
    abstract fun outboxDao(): OutboxDao
}
