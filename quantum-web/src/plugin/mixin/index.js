import { h } from 'vue'

function copyText(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(
            () => window.$message.success('复制成功！'),
            () => window.$message.error('复制失败！')
        )
    } else {
        const textarea = document.createElement('textarea')
        textarea.value = text
        document.body.appendChild(textarea)
        textarea.select()
        try {
            document.execCommand('copy')
            window.$message.success('复制成功！')
        } catch (e) {
            window.$message.error('复制失败！')
        }
        document.body.removeChild(textarea)
    }
}

export default {
    install(app) {
        app.mixin({
            methods: {
                tdTip(h2, params, key, textOrShowOrCallback, messageOrCallback, callback) {
                    let tools = []
                    // 支持多种调用方式：
                    // 1. tdTip(h, params, key) - 复制文本
                    // 2. tdTip(h, params, key, '自定义文本') - 复制自定义文本
                    // 3. tdTip(h, params, key, true, message) - 显示弹窗（message 为显示内容）
                    // 4. tdTip(h, params, key, () => {}) - 自定义点击回调
                    // 5. tdTip(h, params, key, '文本', () => {}) - 自定义文本+回调
                    let text
                    let showModal = false
                    let clickHandler = null

                    // 解析参数
                    if (typeof textOrShowOrCallback === 'function') {
                        // 只有一个回调函数参数
                        clickHandler = textOrShowOrCallback
                        text = params.row[key]
                    } else if (typeof messageOrCallback === 'function') {
                        // textOrShowOrCallback 是文本，messageOrCallback 是回调
                        clickHandler = messageOrCallback
                        text = textOrShowOrCallback || params.row[key]
                    } else if (textOrShowOrCallback === true) {
                        // 显示弹窗模式
                        text = messageOrCallback || params.row[key]
                        showModal = true
                    } else {
                        // 默认复制模式
                        text = textOrShowOrCallback || params.row[key]
                    }

                    if (!text) {
                        return
                    }
                    let that = this
                    tools.push(
                        h(
                            'a',
                            {
                                style: {
                                    display: 'inline-block',
                                    width: '100%',
                                    textIndent: '0px',
                                    overflow: 'hidden',
                                    textOverflow: 'ellipsis',
                                    whiteSpace: 'nowrap'
                                },
                                title: text,
                                onClick: () => {
                                    if (clickHandler) {
                                        clickHandler(params.row, text)
                                    } else if (showModal && that.LogModal) {
                                        that.LogModal.LogContent = text
                                        that.LogModal.Title = '详情'
                                        that.LogModal.Show = true
                                    } else {
                                        copyText(text)
                                    }
                                }
                            },
                            text
                        )
                    )
                    return h('div', { style: { width: '100%' } }, tools)
                }
            }
        })
    }
}
