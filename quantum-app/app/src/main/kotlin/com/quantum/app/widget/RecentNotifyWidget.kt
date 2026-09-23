package com.quantum.app.widget

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.provideContent
import androidx.glance.background
import androidx.glance.layout.Column
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.padding
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import androidx.glance.unit.ColorProvider
import com.quantum.app.core.common.ui.theme.QuantumPalette
import com.quantum.app.core.storage.db.NotificationDao
import com.quantum.app.core.storage.db.NotificationEntity
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.android.EntryPointAccessors
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull

/**
 * 桌面小组件（A5.5 P2 体验包）：最近站内通知列表，Room 直读；
 * 系统按 updatePeriodMillis 刷新，通知入库方可调用 update(receiver) 主动刷新。
 */
class RecentNotifyWidget : GlanceAppWidget() {

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val latest = runCatching { readLatest(context) }.getOrDefault(emptyList())
        provideContent {
            WidgetContent(latest)
        }
    }

    /**
     * §4-10：经 Hilt EntryPoint 复用 App 的同一个 `@Singleton` Database 的 DAO，
     * 不再 `Room.databaseBuilder` 自建第二个连接实例（旧写法绕过 DI 单例、多开连接池且与迁移配置割裂），
     * 也不再 `runBlocking`（provideGlance 本身是挂起函数，直接在 IO 上下文挂起读即可）。
     */
    private suspend fun readLatest(context: Context): List<NotificationEntity> =
        withContext(Dispatchers.IO) {
            val dao = EntryPointAccessors
                .fromApplication(context.applicationContext, WidgetEntryPoint::class.java)
                .notificationDao()
            withTimeoutOrNull(2_000) { dao.page(limit = 5, offset = 0).first() } ?: emptyList()
        }

    @Composable
    private fun WidgetContent(items: List<NotificationEntity>) {
        GlanceTheme {
            Column(
                modifier = GlanceModifier.fillMaxSize()
                    .background(ColorProvider(QuantumPalette.Surface))
                    .padding(10.dp)
            ) {
                Text(
                    "量子助手 · 最近通知",
                    style = TextStyle(color = ColorProvider(QuantumPalette.TextMuted), fontSize = 11.sp),
                    modifier = GlanceModifier.fillMaxWidth().padding(bottom = 6.dp)
                )
                if (items.isEmpty()) {
                    Text(
                        "暂无通知",
                        style = TextStyle(color = ColorProvider(QuantumPalette.TextPrimary), fontSize = 13.sp)
                    )
                } else {
                    items.forEach { item ->
                        Column(modifier = GlanceModifier.fillMaxWidth().padding(vertical = 2.dp)) {
                            Text(
                                "${item.title} · ${item.createdAt.substringAfter(" ").orEmpty()}",
                                style = TextStyle(color = ColorProvider(QuantumPalette.TextPrimary), fontSize = 12.sp),
                                maxLines = 1
                            )
                            Text(
                                item.content,
                                style = TextStyle(color = ColorProvider(QuantumPalette.TextMuted), fontSize = 11.sp),
                                maxLines = 1
                            )
                        }
                    }
                }
            }
        }
    }
}

class RecentNotifyWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = RecentNotifyWidget()
}

/** §4-10：桌面小组件不是 Hilt 构造的，经此 EntryPoint 从 SingletonComponent 取 App 在用的同一 DAO/Database。 */
@EntryPoint
@InstallIn(SingletonComponent::class)
internal interface WidgetEntryPoint {
    fun notificationDao(): NotificationDao
}
