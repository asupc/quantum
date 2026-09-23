package com.quantum.app.feature.notify

import com.quantum.app.core.network.api.AppApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.storage.db.NotificationDao
import com.quantum.app.core.storage.db.NotificationEntity
import kotlinx.coroutines.flow.Flow
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 通知中心仓库：WS notify 帧 / 厂商推送 / REST 列表三来源以 msgId 幂等（Room IGNORE 冲突策略）。
 * 分类 tab（task/system/security 等，未知值按 system 兜底渲染）。
 */
@Singleton
class NotifyRepository @Inject constructor(
    private val appApi: AppApi,
    private val notificationDao: NotificationDao
) {
    fun page(limit: Int = 50, offset: Int = 0): Flow<List<NotificationEntity>> =
        notificationDao.page(limit, offset)

    fun unreadCount(): Flow<Int> = notificationDao.unreadCount()

    /**
     * 统一入库管道：已存在（msgId 相同）返回 false——前台抑制/WS+推送双通道不重复弹。
     */
    suspend fun insertIfAbsent(entity: NotificationEntity): Boolean =
        notificationDao.insertIfAbsent(entity) != -1L

    /** 服务器全量列表拉取（进通知中心时刷新，落库去重）。 */
    suspend fun syncFromServer(): Int {
        val result = appApi.notifications(page = 1, pageSize = 100).unwrap()
        val entities = result.data.map { dto ->
            NotificationEntity(
                msgId = dto.msgId ?: dto.id,
                title = dto.title,
                content = dto.content,
                category = dto.category,
                jump = dto.jump,
                createdAt = dto.createdAt,
                readAt = dto.readAt
            )
        }
        // 批量单事务插入（2.3）：逐条独立事务会让 Room 每行失效一次表，改 INSERT OR IGNORE
        // 批插只失效一次；空批次直接返回不惊动 Room；rowId = -1 表示已存在被忽略，据此统计新增。
        if (entities.isEmpty()) {
            return 0
        }
        return notificationDao.insertAllIfAbsent(entities).count { it != -1L }
    }

    suspend fun markRead(msgId: String) {
        notificationDao.markRead(msgId, now())
        runCatching { appApi.markNotificationRead(msgId) }
    }

    suspend fun markAllRead() {
        notificationDao.markAllRead(now())
        runCatching { appApi.markNotificationRead(null) }
    }

    suspend fun clearLocal() = notificationDao.clear()

    private fun now(): String =
        com.quantum.app.core.common.TimeUtil.nowServerStyle()
}
