<template>
    <div class="tags-nav">
        <div class="close-con">
            <n-dropdown trigger="click" :options="dropdownOptions" @select="handleTagsOption">
                <button class="icon-btn" type="button" title="页签操作">
                    <i class="fa-regular fa-circle-xmark"></i>
                </button>
            </n-dropdown>
        </div>
        <ul v-show="visible" :style="{ left: contextMenuLeft + 'px', top: contextMenuTop + 'px' }" class="contextmenu">
            <li v-for="(item, key) of menuList" @click="handleTagsOption(key)" :key="key">{{ item }}</li>
        </ul>
        <div class="btn-con left-btn">
            <button class="icon-btn" type="button" @click="handleScroll(240)">
                <i class="fa-solid fa-chevron-left"></i>
            </button>
        </div>
        <div class="btn-con right-btn">
            <button class="icon-btn" type="button" @click="handleScroll(-240)">
                <i class="fa-solid fa-chevron-right"></i>
            </button>
        </div>
        <div class="scroll-outer" ref="scrollOuter" @DOMMouseScroll="handlescroll" @mousewheel="handlescroll">
            <div ref="scrollBody" class="scroll-body" :style="{ left: tagBodyLeft + 'px' }">
                <transition-group name="taglist-moving-animation">
                    <span v-for="(item, index) in list" :key="`tag-nav-${index}`" class="tag"
                        :class="{ on: isCurrentTag(item) }" :data-tag-index="index"
                        @click="handleClick(item)" @contextmenu.prevent="contextMenu(item, $event)">
                        <span class="tag-dot"></span>{{ showTitleInside(item) }}
                        <i v-if="item.name !== homeName" class="fa-solid fa-xmark tag-close"
                            @click.stop="handleClose(item)"></i>
                    </span>
                </transition-group>
            </div>
        </div>
    </div>
</template>

<script>
import { showTitle, routeEqual } from '@/libs/util'
import beforeClose from '@/router/before-close'
import { useAppStore } from '@/stores/app'

export default {
    name: 'TagsNav',
    props: {
        value: Object,
        list: {
            type: Array,
            default() {
                return []
            }
        }
    },
    emits: ['input', 'on-close'],
    setup() {
        const appStore = useAppStore()
        return { appStore }
    },
    data() {
        return {
            tagBodyLeft: 0,
            rightOffset: 40,
            outerPadding: 4,
            contextMenuLeft: 0,
            contextMenuTop: 0,
            visible: false,
            menuList: {
                others: '关闭其他',
                all: '关闭所有'
            },
            dropdownOptions: [
                { label: '关闭其他', key: 'close-others' },
                { label: '关闭所有', key: 'close-all' }
            ]
        }
    },
    computed: {
        currentRouteObj() {
            const { name, params, query } = this.value
            return { name, params, query }
        },
        /** 首页页签名（不可关闭）：以菜单管理标记的「默认首页」为准，未标记时回退静态首页 */
        homeName() {
            return this.appStore.homePageName || this.$config.homeName
        }
    },
    methods: {
        handlescroll(e) {
            var type = e.type
            let delta = 0
            if (type === 'DOMMouseScroll' || type === 'mousewheel') {
                delta = (e.wheelDelta) ? e.wheelDelta : -(e.detail || 0) * 40
            }
            this.handleScroll(delta)
        },
        handleScroll(offset) {
            const outerWidth = this.$refs.scrollOuter.offsetWidth
            const bodyWidth = this.$refs.scrollBody.offsetWidth
            if (offset > 0) {
                this.tagBodyLeft = Math.min(0, this.tagBodyLeft + offset)
            } else {
                if (outerWidth < bodyWidth) {
                    if (this.tagBodyLeft < -(bodyWidth - outerWidth)) {
                        this.tagBodyLeft = this.tagBodyLeft
                    } else {
                        this.tagBodyLeft = Math.max(this.tagBodyLeft + offset, outerWidth - bodyWidth)
                    }
                } else {
                    this.tagBodyLeft = 0
                }
            }
        },
        handleTagsOption(key) {
            const type = key.includes('all') ? 'all' : 'others'
            if (type === 'all') {
                // 关闭所有，除了home
                const res = this.list.filter(item => item.name === this.homeName)
                this.$emit('on-close', res, 'all')
            } else {
                // 关闭除当前页和home页的其他页
                const res = this.list.filter(item => routeEqual(this.currentRouteObj, item) || item.name === this.homeName)
                this.$emit('on-close', res, 'others', this.currentRouteObj)
                setTimeout(() => {
                    this.getTagElementByRoute(this.currentRouteObj)
                }, 10)
            }
        },
        handleClose(current) {
            if (current.meta && current.meta.beforeCloseName && current.meta.beforeCloseName in beforeClose) {
                new Promise(beforeClose[current.meta.beforeCloseName]).then(close => {
                    if (close) {
                        this.close(current)
                    }
                })
            } else {
                this.close(current)
            }
        },
        close(route) {
            const res = this.list.filter(item => !routeEqual(route, item))
            this.$emit('on-close', res, undefined, route)
        },
        closeCustomeData(route) {
            const res = this.list.filter(item => item.name == route.name || !item.meta.customDataType || item.name === this.homeName)
            if (res.length != this.list.length) {
                this.$emit('on-close', res, 'all')
            }
            setTimeout(() => {
                this.handleClick(route);
            }, 2);
        },
        handleClick(item) {
            this.$emit('input', item)
        },
        showTitleInside(item) {
            return showTitle(item, this)
        },
        isCurrentTag(item) {
            return routeEqual(this.currentRouteObj, item)
        },
        moveToView(tag) {
            const outerWidth = this.$refs.scrollOuter.offsetWidth
            const bodyWidth = this.$refs.scrollBody.offsetWidth
            if (bodyWidth < outerWidth) {
                this.tagBodyLeft = 0
            } else if (tag.offsetLeft < -this.tagBodyLeft) {
                // 标签在可视区域左侧
                this.tagBodyLeft = -tag.offsetLeft + this.outerPadding
            } else if (tag.offsetLeft > -this.tagBodyLeft && tag.offsetLeft + tag.offsetWidth < -this.tagBodyLeft + outerWidth) {
                // 标签在可视区域
                this.tagBodyLeft = Math.min(0, outerWidth - tag.offsetWidth - tag.offsetLeft - this.outerPadding)
            } else {
                // 标签在可视区域右侧
                this.tagBodyLeft = -(tag.offsetLeft - (outerWidth - this.outerPadding - tag.offsetWidth))
            }
        },
        getTagElementByRoute(route) {
            this.$nextTick(() => {
                const tagEls = this.$refs.scrollBody.querySelectorAll('[data-tag-index]')
                if (!tagEls) return
                tagEls.forEach((el) => {
                    const index = Number(el.getAttribute('data-tag-index'))
                    if (this.list[index] && routeEqual(route, this.list[index])) {
                        this.moveToView(el)
                    }
                })
            })
        },
        contextMenu(item, e) {
            if (item.name === this.homeName) {
                return
            }
            this.visible = true
            const offsetLeft = this.$el.getBoundingClientRect().left
            this.contextMenuLeft = e.clientX - offsetLeft + 10
            this.contextMenuTop = e.clientY - 64
        },
        closeMenu() {
            this.visible = false
        }
    },
    watch: {
        '$route'(to) {
            if (to.meta.customDataType) {
                this.closeCustomeData(to);
            }
            this.getTagElementByRoute(to)
        },
        visible(value) {
            if (value) {
                document.body.addEventListener('click', this.closeMenu)
            } else {
                document.body.removeEventListener('click', this.closeMenu)
            }
        }
    },
    mounted() {
        setTimeout(() => {
            this.getTagElementByRoute(this.$route)
        }, 100)
    },
    beforeUnmount() {
        // §3-14：右键菜单展开时若组件卸载，watcher 不会触发，body 上的 closeMenu 监听会残留并回调到已销毁实例
        document.body.removeEventListener('click', this.closeMenu)
    }
}
</script>

<style scoped>
.tags-nav {
    height: 38px;
    flex: none;
    background: rgba(13, 21, 38, .5);
    border-bottom: 1px solid var(--line);
    display: flex;
    align-items: center;
    padding: 0 12px;
    position: relative;
}

.close-con,
.btn-con {
    flex: none;
    display: flex;
    align-items: center;
    padding: 0 4px;
}

.icon-btn {
    background: transparent;
    border: none;
    color: var(--text-3);
    cursor: pointer;
    font-size: 13px;
    padding: 4px 6px;
    border-radius: 6px;
    display: flex;
    align-items: center;
}

.icon-btn:hover {
    background: rgba(148, 163, 184, .08);
    color: var(--text);
}

.scroll-outer {
    flex: 1;
    align-self: stretch;
    overflow: hidden;
    position: relative;
}

.scroll-body {
    position: absolute;
    top: 50%;
    transform: translateY(-50%);
    display: inline-flex;
    align-items: center;
    gap: 8px;
    padding: 0 4px;
    white-space: nowrap;
    transition: left .2s ease;
}

/* 页签胶囊：对齐 preview-b .tag/.tag.on */
.tag {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    color: var(--text-2);
    border: 1px solid var(--line);
    border-radius: 6px;
    padding: 3px 10px;
    background: var(--card);
    cursor: pointer;
    user-select: none;
}

.tag.on {
    color: var(--accent);
    border-color: rgba(34, 211, 238, .45);
    background: var(--accent-weak);
}

.tag-dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: currentColor;
    box-shadow: 0 0 8px currentColor;
}

.tag-close {
    font-size: 10px;
    opacity: .6;
}

.tag-close:hover {
    opacity: 1;
}

.contextmenu {
    position: absolute;
    z-index: 100;
    margin: 0;
    padding: 6px 0;
    list-style: none;
    background: var(--card);
    border: 1px solid var(--line-2);
    border-radius: 8px;
    box-shadow: 0 10px 30px rgba(0, 0, 0, .5);
}

.contextmenu li {
    padding: 6px 14px;
    font-size: 12px;
    color: var(--text-2);
    cursor: pointer;
}

.contextmenu li:hover {
    background: var(--accent-weak);
    color: var(--accent);
}

.taglist-moving-animation-move {
    transition: transform .3s;
}

.taglist-moving-animation-leave-active {
    position: absolute;
}
</style>
