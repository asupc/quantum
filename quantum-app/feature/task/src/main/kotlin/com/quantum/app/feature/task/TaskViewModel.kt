package com.quantum.app.feature.task

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.quantum.app.core.common.ApiException
import com.quantum.app.core.network.api.AdminApi
import com.quantum.app.core.network.api.unwrap
import com.quantum.app.core.network.dto.TaskDto
import com.quantum.app.core.network.session.SessionManager
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * 任务域（A5.4）：Manager 任务已由服务端按调用者身份过滤（A5.0④），客户端不自行显隐。
 * 手动执行 → 完成一次「执行任务 → 收通知 → 看日志」闭环。
 */
@HiltViewModel
class TaskViewModel @Inject constructor(
    private val adminApi: AdminApi
) : ViewModel() {

    data class TaskPage(val items: List<TaskDto> = emptyList(), val total: Int = 0)

    companion object {
        /** 服务端分页大小（触底加载按此翻页）。 */
        const val PAGE_SIZE = 20
    }

    private val _page = MutableStateFlow(TaskPage())
    val page: StateFlow<TaskPage> = _page.asStateFlow()

    private val _pageIndex = MutableStateFlow(1)
    val pageIndex: StateFlow<Int> = _pageIndex.asStateFlow()
    private val _key = MutableStateFlow("")
    private val _loading = MutableStateFlow(false)
    val loading: StateFlow<Boolean> = _loading.asStateFlow()

    /** 下拉刷新中（与首屏 loading 分开：刷新时列表已有数据，不应换成骨架屏）。 */
    private val _refreshing = MutableStateFlow(false)
    val refreshing: StateFlow<Boolean> = _refreshing.asStateFlow()

    /** 已到底：触底加载更多的终止条件。 */
    private val _endReached = MutableStateFlow(false)
    val endReached: StateFlow<Boolean> = _endReached.asStateFlow()

    /** 列表加载失败文案：空列表时渲染错误态 + 重试（区别于"真的没有数据"）。 */
    private val _error = MutableStateFlow<String?>(null)
    val error: StateFlow<String?> = _error.asStateFlow()

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String?> = _toast.asStateFlow()

    /**
     * 编辑弹层当前编辑的任务（null=未打开）。数据源就是列表行：
     * TaskDto 已覆盖服务端 TaskSaveModel 的全部可写字段，未改动项原样回带，
     * 避免 PUT 时被服务端 ApplyTo 逐字段覆盖成缺省值。
     */
    private val _editing = MutableStateFlow<TaskDto?>(null)
    val editing: StateFlow<TaskDto?> = _editing.asStateFlow()

    init {
        load()
    }

    fun openEditor(task: TaskDto) {
        _editing.value = task
    }

    fun closeEditor() {
        _editing.value = null
    }

    /** 保存编辑：PUT api/Task 整体回写，成功后原位替换行（不整页重载）。 */
    fun saveTask(task: TaskDto) {
        viewModelScope.launch {
            runCatching { adminApi.updateTask(task).unwrap() }
                .onSuccess {
                    replaceRow(task)
                    _editing.value = null
                    _toast.value = "任务已保存"
                }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message ?: "保存失败" }
        }
    }

    /** 列表行原位替换（编辑保存/启停后不重拉列表，避免整页抖动）。 */
    private fun replaceRow(task: TaskDto) {
        _page.value = _page.value.copy(
            items = _page.value.items.map { if (it.id == task.id) task else it }
        )
    }

    fun load(pageIndex: Int = _pageIndex.value, key: String = _key.value, refreshing: Boolean = false) {
        _pageIndex.value = pageIndex
        _key.value = key
        if (refreshing) {
            _refreshing.value = true
        } else {
            _loading.value = true
        }
        viewModelScope.launch {
            runCatching { adminApi.tasks(pageIndex = pageIndex, pageSize = PAGE_SIZE, key = key.ifBlank { null }).unwrap() }
                .onSuccess {
                    _page.value = TaskPage(it.data, it.totalCount)
                    _endReached.value = it.data.size >= it.totalCount
                    _error.value = null
                }
                .onFailure { e ->
                    val message = (e as? ApiException)?.message ?: e.message
                    _error.value = message
                    _toast.value = message
                }
                .also {
                    _loading.value = false
                    _refreshing.value = false
                }
        }
    }

    /** 下拉刷新：回到第一页，保留当前搜索词。 */
    fun refresh() = load(pageIndex = 1, key = _key.value, refreshing = true)

    /**
     * 触底加载下一页：**追加**而非替换（原 PageFooter 翻页已由触底加载取代）。
     * 按 id 去重，避免服务端数据变动时出现重复行。
     */
    fun loadMore() {
        if (_loading.value || _refreshing.value || _endReached.value) {
            return
        }
        val next = _pageIndex.value + 1
        _loading.value = true
        viewModelScope.launch {
            runCatching { adminApi.tasks(pageIndex = next, pageSize = PAGE_SIZE, key = _key.value.ifBlank { null }).unwrap() }
                .onSuccess {
                    _pageIndex.value = next
                    val existing = _page.value.items.map { it.id }.toSet()
                    val merged = _page.value.items + it.data.filterNot { dto -> dto.id in existing }
                    _page.value = TaskPage(merged, it.totalCount)
                    _endReached.value = merged.size >= it.totalCount || it.data.isEmpty()
                }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
                .also { _loading.value = false }
        }
    }

    /** 手动执行（POST exec-task，body 为字符串数组）。执行结果经任务通知（category=task）回到通知中心。 */
    fun exec(taskIds: List<String>) {
        viewModelScope.launch {
            runCatching { adminApi.execTasks(taskIds).unwrap() }
                .onSuccess { _toast.value = "任务已提交执行，结果将在通知中心推送" }
                .onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
        }
    }

    fun toggleEnable(task: TaskDto) {
        viewModelScope.launch {
            runCatching {
                if (task.enable) adminApi.disableTasks(listOf(task.id)).unwrap()
                else adminApi.enableTasks(listOf(task.id)).unwrap()
            }.onSuccess {
                replaceRow(task.copy(enable = !task.enable))
                _toast.value = if (task.enable) "已停用 ${task.name}" else "已启用 ${task.name}"
            }.onFailure { e -> _toast.value = (e as? ApiException)?.message ?: e.message }
        }
    }

    fun consumeToast() {
        _toast.value = null
    }
}
