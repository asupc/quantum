<template>
    <div class="navigation-page">
        <div class="navigation-header">
            <h2></h2>
            <div class="header-actions">
                <n-button @click="showAddModal" type="primary">
                    <template #icon>
                        <i class="fa-solid fa-plus"></i>
                    </template>
                    添加
                </n-button>
                <n-button @click="showImportModal">
                    <template #icon>
                        <i class="fa-solid fa-cloud-arrow-up"></i>
                    </template>
                    导入
                </n-button>
                <n-button @click="toggleViewMode">
                    <template #icon>
                        <i class="fa-solid fa-table-cells"></i>
                    </template>
                    {{ gridView ? '列表视图' : '网格视图' }}
                </n-button>
            </div>
        </div>

        <div class="navigation-content">
            <!-- 搜索框 -->
            <div class="search-box">
                <n-input v-model:value="searchKeyword" placeholder="搜索..." style="width: 300px" clearable
                    @update:value="handleSearch">
                    <template #prefix>
                        <i class="fa-solid fa-magnifying-glass"></i>
                    </template>
                </n-input>
            </div>

            <div class="bookmarks-container">
                <!-- 网格视图 -->
                <div v-if="gridView" class="grid-view">
                    <div class="bookmark-grid">
                        <div v-for="bookmark in filteredBookmarks.filter(n => !n.IsFolder)" :key="bookmark.Id"
                            class="bookmark-card" @click="openBookmark(bookmark)">
                            <div class="bookmark-icon">
                                <img v-if="bookmark.IconUrl" :src="bookmark.IconUrl" :alt="bookmark.Title">
                                <i v-else class="fa-solid fa-link" style="font-size: 32px"></i>
                            </div>
                            <div class="bookmark-info">
                                <h4 :title="bookmark.Title">{{ bookmark.Title }}</h4>
                                <p v-if="bookmark.Description" :title="bookmark.Description">{{ bookmark.Description }}
                                </p>
                            </div>
                            <div class="bookmark-actions">
                                <n-button size="small" @click.stop="editBookmark(bookmark)">
                                    <template #icon>
                                        <i class="fa-solid fa-pen-to-square"></i>
                                    </template>
                                </n-button>
                                <n-button size="small" @click.stop="confirmDelete(bookmark)">
                                    <template #icon>
                                        <i class="fa-solid fa-trash-can"></i>
                                    </template>
                                </n-button>
                            </div>
                        </div>
                    </div>
                </div>

                <!-- 列表视图 -->
                <div v-else class="list-view">
                    <n-data-table :columns="columns" :data="filteredBookmarks" :bordered="true" size="small"
                        :scroll-x="700" />
                </div>
            </div>
        </div>

        <!-- 添加/编辑模态框 -->
        <n-modal preset="card" v-model:show="modalVisible" :title="editingBookmark ? '编辑书签' : '添加书签'"
            style="width: 560px">
            <n-form :model="currentBookmark" label-placement="left" label-width="80">
                <n-form-item label="标题">
                    <n-input v-model:value="currentBookmark.Title" placeholder="请输入书签标题" />
                </n-form-item>
                <n-form-item label="URL">
                    <n-input v-model:value="currentBookmark.Url" placeholder="请输入网址" />
                </n-form-item>
                <n-form-item label="图标">
                    <n-input v-model:value="currentBookmark.IconUrl" placeholder="请输入图标URL" />
                </n-form-item>
                <n-form-item label="描述">
                    <n-input v-model:value="currentBookmark.Description" type="textarea" placeholder="请输入描述" :rows="2" />
                </n-form-item>
                <n-form-item label="排序">
                    <n-input-number v-model:value="currentBookmark.SortOrder" :min="0" />
                </n-form-item>
            </n-form>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="cancelEdit" style="margin-right: 8px">取消</n-button>
                    <n-button type="primary" @click="saveBookmark">确定</n-button>
                </div>
            </template>
        </n-modal>

        <!-- 导入模态框 -->
        <n-modal preset="card" v-model:show="importModalVisible" title="导入书签" style="width: 560px">
            <n-tabs type="line" v-model:value="importTab">
                <n-tab-pane name="html" tab="HTML格式">
                    <n-upload ref="uploadHtml" :before-upload="handleHtmlBeforeUpload" accept=".html,.htm"
                        :default-upload="false" :max="1">
                        <n-button>
                            <template #icon>
                                <i class="fa-solid fa-cloud-arrow-up"></i>
                            </template>
                            选择HTML文件
                        </n-button>
                    </n-upload>
                </n-tab-pane>
                <n-tab-pane name="json" tab="JSON格式">
                    <n-upload ref="upload" :before-upload="handleBeforeUpload" accept=".json" :default-upload="false"
                        :max="1">
                        <n-button>
                            <template #icon>
                                <i class="fa-solid fa-cloud-arrow-up"></i>
                            </template>
                            选择JSON文件
                        </n-button>
                    </n-upload>
                </n-tab-pane>
            </n-tabs>
            <div v-if="importData.length > 0" style="margin-top: 10px">
                <p>将导入 {{ importData.length }} 个书签</p>
            </div>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="cancelImport" style="margin-right: 8px">取消</n-button>
                    <n-button type="primary" @click="doImport">导入</n-button>
                </div>
            </template>
        </n-modal>
    </div>
</template>

<script>
import { h } from 'vue'
import { renderOpActions, OpColor } from '@/utils/op-actions'
import {
    getBookmarks,
    createBookmark,
    updateBookmark,
    deleteBookmark,
    importBookmarks
} from '@/api/bookmark'

export default {
    name: 'NavigationPage',
    data() {
        return {
            loading: false,
            gridView: true,
            gridColumns: 4,
            searchKeyword: '',
            bookmarks: [],
            filteredBookmarks: [],
            modalVisible: false,
            importModalVisible: false,
            importTab: 'html', // 默认选中HTML标签页
            editingBookmark: null,
            importData: [],
            currentBookmark: {
                title: '',
                url: '',
                iconUrl: '',
                description: '',
                parentId: null,
                sortOrder: 0,
                isFolder: false
            },
            columns: [
                {
                    title: '图标',
                    width: 80,
                    render: (row) => {
                        return h('div', { class: 'list-icon' }, [
                            row.IconUrl
                                ? h('img', { src: row.IconUrl, alt: row.Title, width: 24, height: 24, style: { objectFit: 'contain' } })
                                : h('i', { class: 'fa-solid fa-link', style: { fontSize: '24px', color: 'var(--accent)' } })
                        ])
                    }
                },
                {
                    title: '标题',
                    key: 'Title',
                    sorter: (a, b) => String(a.Title).localeCompare(String(b.Title))
                },
                {
                    title: 'URL',
                    key: 'Url',
                    className: 'mono',
                    ellipsis: { tooltip: true }
                },
                {
                    title: '类型',
                    key: 'IsFolder',
                    width: 90,
                    render: (row) => {
                        return h('span', { class: row.IsFolder ? 'pill warn' : 'pill ok' }, [
                            h('span', { class: 'dot' }),
                            row.IsFolder ? '文件夹' : '书签'
                        ])
                    }
                },
                {
                    title: '排序',
                    key: 'SortOrder',
                    width: 80,
                    sorter: (a, b) => (a.SortOrder || 0) - (b.SortOrder || 0)
                },
                {
                    title: '操作',
                    key: 'actions',
                    width: 150,
                    align: 'center',
                    fixed: 'right',
                    render: (row) =>
                        renderOpActions(h, [
                            { icon: 'fa-pencil', title: '编辑', color: OpColor.Edit, onClick: () => this.editBookmark(row) },
                            { icon: 'fa-trash', title: '删除', color: OpColor.Delete, onClick: () => this.confirmDelete(row) }
                        ])
                }
            ]
        }
    },
    computed: {
        folders() {
            return this.bookmarks.filter(b => b.IsFolder)
        }
    },
    mounted() {
        this.loadBookmarks()
    },
    methods: {
        async loadBookmarks() {
            const response = await getBookmarks({
                page: 1,
                pageSize: 1000, // 获取全部数据
                search: this.searchKeyword
            })
            this.bookmarks = response.Data || []
            this.filteredBookmarks = [...this.bookmarks]
        },

        handleSearch() {
            if (!this.searchKeyword) {
                this.filteredBookmarks = [...this.bookmarks]
                return
            }

            this.filteredBookmarks = this.bookmarks.filter(bookmark =>
                (bookmark.Title && bookmark.Title.toLowerCase().includes(this.searchKeyword.toLowerCase())) ||
                (bookmark.Url && bookmark.Url.toLowerCase().includes(this.searchKeyword.toLowerCase()))
            )
        },

        showAddModal() {
            this.editingBookmark = null
            this.currentBookmark = {
                title: '',
                url: '',
                iconUrl: '',
                description: '',
                parentId: null,
                sortOrder: 0,
                isFolder: false
            }
            this.modalVisible = true
        },

        editBookmark(bookmark) {
            this.editingBookmark = bookmark
            this.currentBookmark = { ...bookmark }
            this.modalVisible = true
        },

        async saveBookmark() {
            if (this.editingBookmark) {
                // 更新书签
                await updateBookmark(this.editingBookmark.id, this.currentBookmark)
                this.$message.success('书签更新成功')
            } else {
                // 创建书签
                await createBookmark(this.currentBookmark)
                this.$message.success('书签创建成功')
            }
            this.modalVisible = false
            this.loadBookmarks()
        },

        cancelEdit() {
            this.modalVisible = false
        },

        confirmDelete(bookmark) {
            this.$dialog.warning({
                title: '确认删除',
                content: `确定要删除 "${bookmark.Title}" 吗？`,
                positiveText: '删除',
                negativeText: '取消',
                onPositiveClick: () => {
                    this.deleteBookmark(bookmark)
                }
            })
        },

        async deleteBookmark(bookmark) {
            await deleteBookmark(bookmark.Id)
            this.$message.success('删除成功')
            this.loadBookmarks()
        },

        openBookmark(bookmark) {
            if (bookmark.Url) {
                window.open(bookmark.Url, '_blank')
            }
        },

        toggleViewMode() {
            this.gridView = !this.gridView
        },

        showImportModal() {
            this.importData = []
            this.importModalVisible = true
        },

        handleBeforeUpload({ file }) {
            const reader = new FileReader()
            reader.onload = (e) => {
                try {
                    this.importData = JSON.parse(e.target.result)
                    this.$message.success('JSON文件读取成功，请点击导入')
                } catch (error) {
                    this.$message.error('JSON格式错误: ' + error.message)
                }
            }
            reader.readAsText(file.file)
            return false
        },

        // 处理HTML文件上传
        handleHtmlBeforeUpload({ file }) {
            const reader = new FileReader()
            reader.onload = (e) => {
                try {
                    const htmlContent = e.target.result
                    this.importData = this.parseHtmlBookmarks(htmlContent)
                    this.$message.success(`HTML文件读取成功，解析到 ${this.importData.length} 个书签，请点击导入`)
                } catch (error) {
                    this.$message.error('HTML文件解析失败: ' + error.message)
                }
            }
            reader.readAsText(file.file, 'UTF-8')
            return false
        },

        // 解析HTML书签文件
        parseHtmlBookmarks(htmlContent) {
            const parser = new DOMParser()
            const doc = parser.parseFromString(htmlContent, 'text/html')

            const bookmarks = []
            const dlElements = doc.querySelectorAll('dl')

            // 递归解析书签节点
            const parseDL = (dlElement, parentId = null) => {
                const children = dlElement.children
                for (let i = 0; i < children.length; i++) {
                    const child = children[i]
                    if (child.tagName === 'DT') {
                        const h3Element = child.querySelector('h3')
                        const aElement = child.querySelector('a')

                        if (h3Element) {
                            // 文件夹
                            const folder = {
                                title: h3Element.textContent.trim(),
                                isFolder: true,
                                parentId: parentId,
                                sortOrder: bookmarks.length,
                                id: this.uuid()
                            }
                            bookmarks.push(folder)

                            // 查找子DL元素
                            const nextSibling = child.nextElementSibling
                            if (nextSibling && nextSibling.tagName === 'DL') {
                                parseDL(nextSibling, folder.id)
                            }
                            parentId = folder.id
                        } else if (aElement) {
                            // 书签
                            const bookmark = {
                                title: aElement.textContent.trim(),
                                url: aElement.href,
                                IconUrl: aElement.getAttribute('ICON') || '',
                                description: aElement.getAttribute('description') || '',
                                parentId: parentId,
                                sortOrder: bookmarks.length,
                                isFolder: false,
                                id: this.uuid()
                            }
                            bookmarks.push(bookmark)
                        }
                    }
                }
            }

            // 解析所有DL元素
            dlElements.forEach(dl => {
                // 只处理顶级DL下的内容
                if (dl.parentElement && dl.parentElement.tagName === 'DL') {
                    return
                }
                parseDL(dl)
            })

            return bookmarks
        },
        uuid() {
            var s = [];
            var hexDigits = "0123456789abcdef";
            for (var i = 0; i < 36; i++) {
                s[i] = hexDigits.substr(Math.floor(Math.random() * 0x10), 1);
            }
            s[14] = "4"; // bits 12-15 of the time_hi_and_version field to 0010
            s[19] = hexDigits.substr((s[19] & 0x3) | 0x8, 1); // bits 6-7 of the clock_seq_hi_and_reserved to 01
            s[8] = s[13] = s[18] = s[23] = "-";
            var uuid = s.join("");
            return uuid;
        },
        async doImport() {
            if (this.importData.length === 0) {
                this.$message.warning('请先选择要导入的文件')
                return
            }
            await importBookmarks(this.importData)
            this.$message.success(`成功导入 ${this.importData.length} 个书签`)
            this.importModalVisible = false
            this.loadBookmarks()
        },

        cancelImport() {
            this.importModalVisible = false
        }
    }
}
</script>

<style scoped>
.navigation-page {
    padding: 20px;
    height: 100%;
    overflow-y: auto;
    box-sizing: border-box;
}

.navigation-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 20px;
    flex-wrap: wrap;
}

.navigation-header h2 {
    margin: 0;
    flex: 1;
}

.header-actions {
    display: flex;
    gap: 10px;
}

.search-box {
    margin-bottom: 20px;
    display: flex;
    justify-content: center;
}

.bookmark-grid {
    display: grid;
    gap: 10px;
    grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
    align-items: stretch;
}

.bookmark-card {
    border: 1px solid var(--line);
    border-radius: var(--radius);
    background: linear-gradient(180deg, var(--card) 0%, var(--card-2) 100%);
    padding: 12px;
    cursor: pointer;
    transition: all 0.3s;
    position: relative;
    display: flex;
    flex-direction: column;
    min-height: 120px;
}

.bookmark-card:hover {
    box-shadow: 0 0 20px rgba(34, 211, 238, .15);
    border-color: rgba(34, 211, 238, .45);
    transform: translateY(-2px);
}

.bookmark-icon {
    text-align: center;
    margin-bottom: 12px;
    flex-grow: 1;
    display: flex;
    align-items: center;
    justify-content: center;
}

.bookmark-icon img {
    max-width: 64px;
    max-height: 64px;
    object-fit: contain;
}

.bookmark-icon i {
    font-size: 32px;
    color: var(--accent);
}

.bookmark-info {
    text-align: center;
}

.bookmark-info h4 {
    font-size: 12px;
    margin: 0 0 4px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--text);
}

.bookmark-info p {
    font-size: 12px;
    color: var(--text-3);
    margin: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    min-height: 28px;
}

.bookmark-actions {
    position: absolute;
    top: 8px;
    right: 8px;
    display: none;
    gap: 4px;
}

.bookmark-card:hover .bookmark-actions {
    display: flex;
}

.list-view .list-icon {
    display: flex;
    align-items: center;
    justify-content: center;
}
</style>
