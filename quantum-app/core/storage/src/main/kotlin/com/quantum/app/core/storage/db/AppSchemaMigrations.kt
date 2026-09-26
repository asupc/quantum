package com.quantum.app.core.storage.db

/**
 * Room 库版本升级的 SQL 清单（与 [QuantumDatabase] 分离，单独成文件）：
 * 拆出来只为一个目的——纯 JVM 单测可以引用本对象跑真 SQLite 验证迁移，
 * 而不必加载带 androidx.room 注解的数据库类。
 */
object AppSchemaMigrations {

    // v7 → v8（G-Push 会话标题）
    /**
     * 两条 `ADD COLUMN`，均可空：存量行留 NULL 即按旧规则渲染（任务会话仍取任务名）。
     *
     * 刻意不重建表、不清数据：本库虽被当作可重建缓存，但 `chat_outbox` 的待发正文与
     * `chat_message.pickedKeys` 的选项已选态**服务端没有副本**，一旦被 destructive 重建就永久丢失。
     * 这也是 v7→v8 必须走显式迁移、不能沿用 fallbackToDestructiveMigration 的原因。
     */
    val V7_TO_V8 = listOf(
        "ALTER TABLE `chat_message` ADD COLUMN `sessionTitle` TEXT",
        "ALTER TABLE `chat_session` ADD COLUMN `displayTitle` TEXT"
    )

    // 表 → 本次新增列（列名与实体属性名逐一对应，漏改即由单测判失败）
    val V7_TO_V8_ADDED_COLUMNS = mapOf(
        "chat_message" to listOf("sessionTitle"),
        "chat_session" to listOf("displayTitle")
    )
}
