package com.quantum.app.core.storage.db

import androidx.room.Database
import androidx.room.RoomDatabase
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

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
    // v8（G-Push）：chat_message 加 sessionTitle、chat_session 加 displayTitle。
    // **v7→v8 必须走显式非破坏迁移（[MIGRATION_7_8]）**：本库虽是可重建缓存，但破坏性重建会连带清掉
    // outbox 待发正文与选项已选态（这两样服务端没有副本），故本次变更不接受 destructive fallback。
    // 更早版本之间的历史跳跃仍由 fallbackToDestructiveMigration 兜底（保留它只为不让老装机版本差炸启动）。
    version = 8,
    exportSchema = false
)
abstract class QuantumDatabase : RoomDatabase() {
    abstract fun chatMessageDao(): ChatMessageDao
    abstract fun chatSessionDao(): ChatSessionDao
    abstract fun notificationDao(): NotificationDao
    abstract fun syncCursorDao(): SyncCursorDao
    abstract fun outboxDao(): OutboxDao

    companion object {
        /**
         * v7 → v8（G-Push 会话标题）：两条 ADD COLUMN，均为可空列，存量行留 NULL 即按旧规则渲染。
         * 不重建表、不清数据：outbox 待发正文与选项已选态必须原样活过这次升级。
         */
        val MIGRATION_7_8 = object : Migration(7, 8) {
            override fun migrate(db: SupportSQLiteDatabase) {
                AppSchemaMigrations.V7_TO_V8.forEach { db.execSQL(it) }
            }
        }

        /** 全部显式迁移（注册顺序无关，Room 按 startVersion→endVersion 串联）。 */
        val ALL_MIGRATIONS = arrayOf(MIGRATION_7_8)
    }
}
