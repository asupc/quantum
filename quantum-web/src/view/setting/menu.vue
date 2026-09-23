<template>
    <div>
        <div style="margin-bottom:12px">
            <n-button type="warning" @click="resetMenus">
                <template #icon>
                    <i class="fa-solid fa-arrows-rotate"></i>
                </template>
                重置菜单
            </n-button>
        </div>

        <!-- 菜单树 -->
        <n-card v-if="menus.length > 0" class="menu-card">
            <div v-for="(menu, index) in menus" :key="menu.name + index" class="menu-item-wrapper">
                <!-- 顶级菜单 -->
                <div class="menu-row level-1">
                    <div class="cell expand-cell">
                        <i :class="expandedKeys[menu.name] ? 'fa-solid fa-angle-down' : 'fa-solid fa-angle-right'"
                            class="expand-icon" @click="toggleExpand(menu.name)"></i>
                    </div>
                    <div class="cell title-cell">
                        <i :class="getIconClass(menu)" style="margin-right:4px;color:var(--accent);"></i>
                        <span class="menu-title">{{ getMenuTitle(menu) }}</span>
                        <span v-if="isDefault(menu)" class="home-badge">首页</span>
                    </div>
                    <div class="cell path-cell mono">{{ menu.path || '-' }}</div>
                    <div class="cell sort-cell">{{ menu.sort }}</div>
                    <div class="cell children-cell">
                        <span v-if="menu.children && menu.children.length">{{ menu.children.length }}个子菜单</span>
                        <span v-else>-</span>
                    </div>
                    <div class="cell status-cell">
                        <span :class="getMenuStatus(menu) === 'error' ? 'pill danger' : 'pill ok'">
                            <span class="dot"></span>{{ getMenuStatusText(menu) }}
                        </span>
                    </div>
                    <div class="cell action-cell">
                        <n-button type="primary" size="small" @click="edit(menu)" style="margin-right:4px;">
                            <template #icon>
                                <i class="fa-solid fa-pen-to-square"></i>
                            </template>
                            编辑
                        </n-button>
                        <n-button :type="menu.meta && menu.meta.hideInMenu === 'true' ? 'success' : 'warning'"
                            size="small" @click="toggleHide(menu)">
                            <template #icon>
                                <i :class="menu.meta && menu.meta.hideInMenu === 'true' ? 'fa-solid fa-eye' : 'fa-solid fa-eye-slash'"></i>
                            </template>
                            {{ menu.meta && menu.meta.hideInMenu === 'true' ? '显示' : '隐藏' }}
                        </n-button>
                    </div>
                </div>

                <!-- 子菜单列表 -->
                <div v-if="expandedKeys[menu.name] && menu.children && menu.children.length" class="children-wrapper">
                    <div v-for="(child, childIndex) in menu.children" :key="child.name + childIndex"
                        class="menu-row level-2">
                        <div class="cell expand-cell"><span class="no-expand">-</span></div>
                        <div class="cell title-cell">
                            <i :class="getIconClass(child)" style="margin-right:4px;color:var(--ok);"></i>
                            <span class="menu-title">{{ getMenuTitle(child) }}</span>
                            <span v-if="isDefault(child)" class="home-badge">首页</span>
                        </div>
                        <div class="cell path-cell mono">{{ child.path || '-' }}</div>
                        <div class="cell sort-cell">{{ child.sort }}</div>
                        <div class="cell children-cell">-</div>
                        <div class="cell status-cell">
                            <span :class="getMenuStatus(child) === 'error' ? 'pill danger' : 'pill ok'">
                                <span class="dot"></span>{{ getMenuStatusText(child) }}
                            </span>
                        </div>
                        <div class="cell action-cell">
                            <n-button type="primary" size="small" @click="edit(child)" style="margin-right:4px;">
                                <template #icon>
                                    <i class="fa-solid fa-pen-to-square"></i>
                                </template>
                                编辑
                            </n-button>
                            <n-button :type="child.meta && child.meta.hideInMenu === 'true' ? 'success' : 'warning'"
                                size="small" @click="toggleHide(child)">
                                <template #icon>
                                    <i :class="child.meta && child.meta.hideInMenu === 'true' ? 'fa-solid fa-eye' : 'fa-solid fa-eye-slash'"></i>
                                </template>
                                {{ child.meta && child.meta.hideInMenu === 'true' ? '显示' : '隐藏' }}
                            </n-button>
                        </div>
                    </div>
                </div>
            </div>
        </n-card>

        <n-alert v-else type="warning" show-icon>暂无菜单数据，请点击"刷新"按钮加载</n-alert>

        <!-- 新建/编辑菜单弹窗 -->
        <n-modal preset="card" v-model:show="showModal" :title="modalTitle" style="width: 700px" :mask-closable="false">
            <n-form ref="menuForm" :model="form" label-placement="left" label-width="100">
                <n-form-item label="路由路径">
                    <n-input v-model:value="form.path" disabled placeholder="例如 /settings/menu" />
                </n-form-item>
                <n-form-item label="路由名称">
                    <n-input v-model:value="form.name" disabled placeholder="唯一标识，如 settings-menu" />
                </n-form-item>
                <n-form-item label="组件路径">
                    <n-input v-model:value="form.component" disabled placeholder="相对于 view 目录，如 setting/index" />
                </n-form-item>
                <n-form-item label="父级菜单">
                    <n-select :value="form._parentName" :options="parentOptions" style="width: 260px"
                        :disabled="form.component === 'Main'" placeholder="选择所属分组"
                        @update:value="onParentChange" />
                    <span class="form-tip" v-if="form.component === 'Main'">分组菜单固定为顶级</span>
                    <span class="form-tip" v-else>修改后菜单将移动到所选分组下（路由变为 /分组路径/子路径）</span>
                </n-form-item>
                <n-form-item label="菜单标题">
                    <n-input v-model:value="form.meta.title" placeholder="菜单显示标题" />
                </n-form-item>
                <n-form-item label="排序">
                    <n-input-number v-model:value="form.sort" :min="0" style="width: 200px;" />
                    <span style="margin-left: 10px; color: var(--text-3);">数字越小越靠前</span>
                </n-form-item>
                <n-form-item label="图标">
                    <div class="icon-selector">
                        <n-input v-model:value="form.meta.icon" placeholder="图标类名，如 fa-solid fa-house"
                            style="width: 250px;" />
                        <n-button @click="showIconSelector = true" style="margin-left: 10px;">选择图标</n-button>
                        <span v-if="form.meta.icon" style="margin-left: 10px;">
                            <i :class="form.meta.icon"></i> 预览
                        </span>
                    </div>
                </n-form-item>
                <n-form-item label="隐藏菜单">
                    <n-switch v-model:value="form.hideInMenu" checked-value="true" unchecked-value="false">
                        <template #checked>是</template>
                        <template #unchecked>否</template>
                    </n-switch>
                </n-form-item>
                <n-form-item label="隐藏面包屑">
                    <n-switch v-model:value="form.hideInBread" checked-value="true" unchecked-value="false">
                        <template #checked>是</template>
                        <template #unchecked>否</template>
                    </n-switch>
                </n-form-item>
                <n-form-item label="默认首页">
                    <n-switch v-model:value="form.isDefault" :disabled="form.component === 'Main'"
                        checked-value="true" unchecked-value="false">
                        <template #checked>是</template>
                        <template #unchecked>否</template>
                    </n-switch>
                    <span class="form-tip">
                        {{ form.component === 'Main'
                            ? '分组菜单不能作为首页，请在其子菜单上设置'
                            : '登录后与点击「首页」时进入该页；全局唯一，设置后自动取消其他菜单的首页标记' }}
                    </span>
                </n-form-item>
            </n-form>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="showModal = false" style="margin-right: 8px">取消</n-button>
                    <n-button type="primary" @click="submit">保存</n-button>
                </div>
            </template>
        </n-modal>

        <!-- 图标选择器 -->
        <n-modal preset="card" v-model:show="showIconSelector" title="选择图标" style="width: 800px">
            <div class="icon-search">
                <n-input v-model:value="fasSearch" placeholder="搜索图标..." style="width: 300px;" clearable />
            </div>
            <div class="icon-grid">
                <div v-for="icon in filteredFasIcons" :key="icon" class="icon-item" @click="selectIcon('fa ' + icon)">
                    <i :class="'fa ' + icon" style="font-size: 20px;"></i>
                    <div class="icon-name">{{ icon.replace('fa-', '') }}</div>
                </div>
            </div>
            <template #footer>
                <div style="text-align: right">
                    <n-button @click="showIconSelector = false">关闭</n-button>
                </div>
            </template>
        </n-modal>
    </div>
</template>

<script>
    import {
        GetMenus, UpdateMenu, ResetMenu
    } from '@/api/menu'
    import { useAppStore } from '@/stores/app'
    import { reloadDynamicRoutes } from '@/router'

    export default {
        name: 'SettingsMenu',
        setup() {
            const appStore = useAppStore()
            return { appStore }
        },
        data() {
            return {
                menus: [],
                expandedKeys: {},
                showModal: false,
                showIconSelector: false,
                modalTitle: '新建菜单',
                form: {
                    id: '',
                    path: '',
                    name: '',
                    component: '',
                    sort: 0,
                    hideInMenu: 'false',
                    hideInBread: 'false',
                    isDefault: 'false',
                    meta: {
                        title: '',
                        icon: ''
                    },
                    children: []
                },
                fasSearch: '',
                commonIcons: [
                    "fa-home", "fa-user", "fa-cog", "fa-gear", "fa-bars", "fa-list", "fa-th", "fa-th-large",
                    "fa-sitemap", "fa-folder", "fa-folder-open", "fa-file", "fa-file-alt", "fa-file-text",
                    "fa-edit", "fa-pencil", "fa-trash", "fa-plus", "fa-minus", "fa-times", "fa-check", "fa-search",
                    "fa-database", "fa-server", "fa-laptop", "fa-desktop", "fa-mobile", "fa-tablet", "fa-tv",
                    "fa-microchip", "fa-hdd", "fa-wifi", "fa-bluetooth", "fa-globe", "fa-globe-americas",
                    "fa-map", "fa-map-marker", "fa-compass", "fa-cloud", "fa-cloud-download", "fa-cloud-upload",
                    "fa-download", "fa-upload", "fa-refresh", "fa-sync", "fa-clock", "fa-calendar", "fa-bell",
                    "fa-envelope", "fa-phone", "fa-volume", "fa-microphone", "fa-headphones",
                    "fa-music", "fa-video", "fa-image", "fa-images", "fa-picture",
                    "fa-palette", "fa-code", "fa-code-branch", "fa-terminal", "fa-shield",
                    "fa-lock", "fa-lock-open", "fa-key", "fa-unlock", "fa-bolt", "fa-fire",
                    "fa-rocket", "fa-plane", "fa-tasks",
                    "fa-chart-bar", "fa-chart-line", "fa-chart-pie",
                    "fa-github", "fa-git", "fa-gitlab", "fa-slack", "fa-discord",
                    "fa-star", "fa-heart", "fa-flag", "fa-book", "fa-graduation-cap",
                    "fa-certificate", "fa-trophy", "fa-award", "fa-suitcase",
                    "fa-shopping-cart", "fa-gift", "fa-tag", "fa-tags",
                    "fa-coffee", "fa-food", "fa-glass", "fa-utensils",
                    "fa-futbol", "fa-basketball", "fa-gamepad",
                    "fa-dragon", "fa-cat", "fa-dog", "fa-paw", "fa-bug",
                    "fa-leaf", "fa-tree", "fa-sun", "fa-moon", "fa-snowflake",
                    "fa-umbrella", "fa-truck", "fa-car", "fa-bicycle",
                    "fa-hospital", "fa-medkit", "fa-heartbeat", "fa-brain",
                    "fa-eye", "fa-wheelchair"
                ]
            }
        },
        computed: {
            filteredFasIcons() {
                if (!this.fasSearch) return this.commonIcons;
                return this.commonIcons.filter(icon =>
                    icon.toLowerCase().includes(this.fasSearch.toLowerCase())
                );
            },
            // 可作为父级的菜单：仅分组（component=Main）。子菜单挂到分组下才渲染在 Main 布局内，
            // 裸页面提为顶级会脱离侧栏布局，故不提供「无父级」选项。
            parentOptions() {
                return this.menus
                    .filter(m => m.component === 'Main' && m.name !== this.form._originalName)
                    .map(m => ({ label: (m.meta && m.meta.title) || m.name, value: m.name }))
            }
        },
        methods: {
            load(keepExpanded = false) {
                // 保存当前的展开状态
                const savedExpandedKeys = keepExpanded ? { ...this.expandedKeys } : {}

                GetMenus().then(res => {
                    this.menus = res || []

                    if (keepExpanded && Object.keys(savedExpandedKeys).length > 0) {
                        // 恢复之前的展开状态，只保留仍然存在的菜单
                        this.expandedKeys = {}
                        this.menus.forEach(m => {
                            this.expandedKeys[m.name] = savedExpandedKeys[m.name] === true
                        })
                    } else {
                        // 初始化所有顶级菜单为收起状态
                        this.expandedKeys = {}
                        this.menus.forEach(m => {
                            this.expandedKeys[m.name] = false
                        })
                    }
                })
            },
            toggleExpand(name) {
                this.expandedKeys[name] = !this.expandedKeys[name]
            },
            getMenuTitle(menu) {
                return (menu.meta && menu.meta.title) || menu.name || '未命名'
            },
            getIconClass(menu) {
                if (menu.meta && menu.meta.icon) {
                    return menu.meta.icon
                }
                return 'fa-solid fa-folder'
            },
            getMenuStatus(menu) {
                return (menu.meta && menu.meta.hideInMenu) === 'true' ? 'error' : 'success'
            },
            getMenuStatusText(menu) {
                return (menu.meta && menu.meta.hideInMenu) === 'true' ? '已隐藏' : '正常'
            },
            openAdd() {
                this.modalTitle = '新建菜单'
                this.form = {
                    id: '',
                    path: '',
                    name: '',
                    component: '',
                    sort: this.menus.length + 1,
                    hideInMenu: 'false',
                    hideInBread: 'false',
                    isDefault: 'false',
                    meta: {
                        title: '',
                        icon: ''
                    },
                    children: []
                }
                this.showModal = true
            },
            addChild(parent) {
                this.modalTitle = '新建子菜单'
                this.form = {
                    id: '',
                    path: '',
                    name: '',
                    component: '',
                    sort: (parent.children ? parent.children.length : 0) + 1,
                    hideInMenu: 'false',
                    hideInBread: 'false',
                    isDefault: 'false',
                    meta: {
                        title: '',
                        icon: ''
                    },
                    children: [],
                    _parentName: parent.name
                }
                this.showModal = true
            },
            edit(row) {
                this.modalTitle = '编辑菜单'
                this.form = {
                    id: row.id || '',
                    path: row.path || '',
                    name: row.name || '',
                    component: row.component || '',
                    sort: row.sort || 0,
                    hideInMenu: (row.meta && row.meta.hideInMenu) || 'false',
                    hideInBread: (row.meta && row.meta.hideInBread) || 'false',
                    isDefault: (row.meta && row.meta.isDefault) || 'false',
                    meta: {
                        title: (row.meta && row.meta.title) || '',
                        icon: (row.meta && row.meta.icon) || ''
                    },
                    children: row.children || [],
                    _originalName: row.name,
                    // 记录编辑前的父级：submit 时据此判断是否发生了跨分组移动
                    _origParentName: this.findParentName(this.menus, row.name)
                }
                this.form._parentName = this.form._origParentName
                this.showModal = true
            },
            /** 在菜单树中查某节点的父级分组名；顶级返回 ''，找不到返回 null（名字唯一是菜单保存的前提） */
            findParentName(menus, name, parent = '') {
                for (const menu of menus) {
                    if (menu.name === name) return parent
                    if (menu.children && menu.children.length) {
                        const found = this.findParentName(menu.children, name, menu.name)
                        if (found !== null) return found
                    }
                }
                return null
            },
            /** 按路由名称查菜单节点（含任意层级） */
            findMenu(menus, name) {
                for (const menu of menus) {
                    if (menu.name === name) return menu
                    if (menu.children && menu.children.length) {
                        const found = this.findMenu(menu.children, name)
                        if (found) return found
                    }
                }
                return null
            },
            /** 按路由名称从菜单树中摘除节点并返回（跨分组移动用） */
            findAndRemove(menus, name) {
                for (let i = 0; i < menus.length; i++) {
                    if (menus[i].name === name) {
                        return menus.splice(i, 1)[0]
                    }
                    if (menus[i].children && menus[i].children.length) {
                        const removed = this.findAndRemove(menus[i].children, name)
                        if (removed) return removed
                    }
                }
                return null
            },
            /** 弹窗里切换父级：排序号自动顺延到目标分组末尾，避免移动后挤占既有位置 */
            onParentChange(name) {
                this.form._parentName = name
                if (!name) return
                const target = this.findMenu(this.menus, name)
                if (target && target.children) {
                    this.form.sort = target.children.length + 1
                }
            },
            selectIcon(icon) {
                this.form.meta.icon = icon
                this.showIconSelector = false
            },
            submit() {
                const menuData = JSON.parse(JSON.stringify(this.menus))
                // 默认首页全局唯一：先清掉整棵树上旧的 isDefault，再由下面的分支写到目标项上
                // （顺序不能反：先写后清会把刚设上的标记一起删掉）
                this.clearDefaultFlag(menuData)

                // 分支判定顺序：编辑(_originalName)必须先于新建子菜单(_parentName)——
                // edit() 也会带 _parentName（当前父级），先判 _parentName 会把编辑误走成「添加」造成重复行
                if (this.form._originalName) {
                    // 编辑现有菜单：父级变化时先从原分组摘除再挂到新分组，未变化则原位替换（保持顺序）
                    const updated = {
                        id: this.form.id || '',
                        path: this.form.path,
                        name: this.form.name,
                        component: this.form.component,
                        sort: this.form.sort,
                        meta: {
                            title: this.form.meta.title,
                            icon: this.form.meta.icon,
                            hideInMenu: this.form.hideInMenu,
                            hideInBread: this.form.hideInBread,
                            isDefault: this.form.isDefault
                        },
                        children: []
                    }
                    if (this.form._parentName !== this.form._origParentName) {
                        const original = this.findAndRemove(menuData, this.form._originalName)
                        if (!original) {
                            this.$message.error('未找到原菜单，请刷新后重试')
                            return
                        }
                        updated.children = original.children || []
                        const target = this.findMenu(menuData, this.form._parentName)
                        // 同分组下相同子路径会互相覆盖路由，先拦住（vue-router addRoute 同路径静默覆盖）
                        if (target && (target.children || []).some(c => c.path === updated.path)) {
                            this.$message.error(`目标分组下已存在相同路由路径「${updated.path}」，无法移动`)
                            return
                        }
                        if (target) {
                            target.children = target.children || []
                            target.children.push(updated)
                        } else {
                            menuData.push(updated)
                        }
                    } else {
                        this.findAndUpdate(menuData, this.form._originalName, updated)
                    }
                } else if (this.form._parentName) {
                    // 添加子菜单（addChild 只设 _parentName，不带 _originalName）
                    this.findAndAddChild(menuData, this.form._parentName, {
                        id: this.form.id || '',
                        path: this.form.path,
                        name: this.form.name,
                        component: this.form.component,
                        sort: this.form.sort,
                        meta: {
                            title: this.form.meta.title,
                            icon: this.form.meta.icon,
                            hideInMenu: this.form.hideInMenu,
                            hideInBread: this.form.hideInBread,
                            isDefault: this.form.isDefault
                        },
                        children: []
                    })
                } else {
                    // 新建顶级菜单
                    menuData.push({
                        id: this.form.id || '',
                        path: this.form.path,
                        name: this.form.name,
                        component: this.form.component,
                        sort: this.form.sort,
                        meta: {
                            title: this.form.meta.title,
                            icon: this.form.meta.icon,
                            hideInMenu: this.form.hideInMenu,
                            hideInBread: this.form.hideInBread,
                            isDefault: this.form.isDefault
                        },
                        children: []
                    })
                }

                // 默认首页全局唯一：整棵树的旧 isDefault 已在上方清掉，这里只决定本次落点
                const previousHome = this.appStore.homePageName
                // 本次编辑后的首页：设为首页=该项；取消当前首页=回退静态首页；其他情况不变
                const nextHome = this.form.isDefault === 'true'
                    ? this.form.name
                    : (previousHome === this.form.name ? '' : previousHome)

                UpdateMenu(menuData).then(() => {
                    this.$message.success('保存成功')
                    this.showModal = false
                    this.load(true) // 保持当前展开状态
                    this.applyHomeChange(previousHome, nextHome)
                }).catch(() => {
                    this.$message.error('保存失败')
                })
            },
            /** 递归清除所有节点上的默认首页标记 */
            clearDefaultFlag(list) {
                list.forEach((item) => {
                    if (item.meta) {
                        delete item.meta.isDefault
                    }
                    if (item.children && item.children.length) {
                        this.clearDefaultFlag(item.children)
                    }
                })
            },
            /** 某个菜单是否被标记为默认首页（后端以字符串 'true' 往返） */
            isDefault(row) {
                return !!(row && row.meta && (row.meta.isDefault === 'true' || row.meta.isDefault === true))
            },
            /**
             * 首页标记变化后即时生效：重载前端菜单路由、重算首页并跳到新首页。
             * 前端菜单路由是加载一次后缓存的，不重载就要刷新整页才生效。
             */
            applyHomeChange(previousHome, nextHome) {
                if (nextHome === previousHome) {
                    return
                }
                reloadDynamicRoutes().then((routes) => {
                    this.appStore.setDynamicRoutes(routes)
                    this.appStore.setHomeRoute(routes)
                    const path = this.appStore.homePagePath
                    window.$message?.success(nextHome ? '已设置默认首页，正在跳转…' : '已取消默认首页，回退静态首页')
                    if (path) {
                        this.$router.push({ path }).catch(() => {})
                    }
                })
            },
            findAndAddChild(menus, parentName, newChild) {
                for (const menu of menus) {
                    if (menu.name === parentName) {
                        if (!menu.children) menu.children = []
                        menu.children.push(newChild)
                        return true
                    }
                    if (menu.children && menu.children.length > 0) {
                        if (this.findAndAddChild(menu.children, parentName, newChild)) {
                            return true
                        }
                    }
                }
                return false
            },
            findAndUpdate(menus, originalName, updated) {
                for (let i = 0; i < menus.length; i++) {
                    if (menus[i].name === originalName) {
                        updated.children = menus[i].children || []
                        menus[i] = updated
                        return true
                    }
                    if (menus[i].children && menus[i].children.length > 0) {
                        if (this.findAndUpdate(menus[i].children, originalName, updated)) {
                            return true
                        }
                    }
                }
                return false
            },
            toggleHide(row) {
                const menuData = JSON.parse(JSON.stringify(this.menus))
                const updateHide = (menus) => {
                    for (const menu of menus) {
                        if (menu.name === row.name) {
                            menu.meta = menu.meta || {}
                            menu.meta.hideInMenu = menu.meta.hideInMenu === 'true' ? 'false' : 'true'
                            return true
                        }
                        if (menu.children && menu.children.length > 0) {
                            if (updateHide(menu.children)) {
                                return true
                            }
                        }
                    }
                    return false
                }
                updateHide(menuData)

                UpdateMenu(menuData).then(() => {
                    this.$message.success(row.meta && row.meta.hideInMenu === 'true' ? '已显示' : '已隐藏')
                    this.load(true) // 保持当前展开状态
                }).catch(() => {
                    this.$message.error('操作失败')
                })
            },
            resetMenus() {
                this.$dialog.warning({
                    title: '确认重置',
                    content: '确定要重置菜单到默认状态吗？这将恢复所有菜单项。',
                    positiveText: '确定',
                    negativeText: '取消',
                    onPositiveClick: () => {
                        ResetMenu().then(() => {
                            this.$message.success('菜单已重置')
                            this.load()
                        }).catch(() => {
                            this.$message.error('重置失败')
                        })
                    }
                })
            }
        },
        mounted() {
            this.load()
        }
    }
</script>

<style scoped>
    .menu-card {
        margin-bottom: 12px;
    }

    .menu-item-wrapper {
        border-bottom: 1px solid var(--line);
    }

    .menu-item-wrapper:last-child {
        border-bottom: none;
    }

    .menu-row {
        padding: 10px 0;
        display: flex;
        align-items: center;
    }

    .menu-row.level-2 {
        background: rgba(148, 163, 184, .04);
        border-top: 1px dashed var(--line);
    }

    .cell.expand-cell {
        width: 3%;
        text-align: center;
    }

    .cell.title-cell {
        width: 22%;
    }

    .cell.path-cell {
        width: 22%;
        color: var(--text-2);
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
    }

    .cell.sort-cell {
        width: 8%;
        text-align: center;
    }

    .cell.children-cell {
        width: 10%;
        text-align: center;
        color: var(--accent-2);
    }

    .cell.status-cell {
        width: 10%;
    }

    .cell.action-cell {
        width: 25%;
        text-align: right;
    }

    .expand-icon {
        cursor: pointer;
        color: var(--accent);
    }

    .expand-icon:hover {
        color: var(--accent-2);
    }

    .no-expand {
        color: var(--text-3);
    }

    .menu-title {
        font-weight: 500;
        color: var(--text);
    }

    /* 默认首页标记：与「首页」页签同语义，登录后进入该页 */
    .home-badge {
        margin-left: 6px;
        padding: 1px 7px;
        border-radius: 999px;
        font-size: 11px;
        line-height: 16px;
        color: var(--accent);
        background: var(--accent-weak);
        border: 1px solid rgba(14, 165, 233, .4);
    }

    .form-tip {
        margin-left: 10px;
        font-size: 12px;
        color: var(--text-3);
    }

    .icon-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(90px, 1fr));
        gap: 10px;
        max-height: 350px;
        overflow-y: auto;
        padding: 10px 0;
    }

    .icon-item {
        text-align: center;
        padding: 10px 5px;
        border: 1px solid var(--line);
        border-radius: 4px;
        cursor: pointer;
        transition: all 0.2s;
        color: var(--text);
    }

    .icon-item:hover {
        border-color: var(--accent);
        background-color: var(--accent-weak);
    }

    .icon-name {
        font-size: 11px;
        margin-top: 5px;
        color: var(--text-2);
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
    }

    .icon-search {
        margin-bottom: 12px;
    }
</style>
