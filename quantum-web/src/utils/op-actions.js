// 表格行内操作列统一渲染：纯图标 + 悬浮提示 + 语义色（风格 B 设计 token）
// 用法：render: (row) => renderOpActions(h, [
//   { icon: 'fa-pencil', title: '编辑', color: OpColor.Edit, onClick: () => this.edit(row) },
//   { icon: 'fa-trash', title: '删除', color: OpColor.Delete, onClick: () => this.del(row.Id) }
// ])

export const OpColor = {
    Edit: 'var(--warn)',     // 编辑/修改
    Delete: 'var(--danger)', // 删除
    Run: 'var(--ok)',        // 执行/启动
    Info: 'var(--accent)'    // 其他中性操作
}

// actions: [{ icon, title, color, onClick }]，项为 false/null 时跳过（用于按状态显隐单个操作）
export function renderOpActions(h, actions) {
    const icons = (actions || []).filter(Boolean).map(a =>
        h('i', {
            class: `op-icon fa-solid ${a.icon}`,
            title: a.title,
            style: a.color ? { color: a.color } : undefined,
            onClick: a.onClick
        })
    )
    return h('div', { class: 'op-actions' }, icons)
}
