import { darkTheme } from 'naive-ui'

export const theme = darkTheme

// 风格 B「暗夜量子 · 钛金石墨」Naive UI 主题覆盖 —— 与 src/styles/tokens.css 同步
export const themeOverrides = {
    common: {
        primaryColor: '#0EA5E9',
        primaryColorHover: '#38BDF8',
        primaryColorPressed: '#0284C7',
        primaryColorSuppl: '#0284C7',
        infoColor: '#38BDF8',
        successColor: '#10B981',
        warningColor: '#F59E0B',
        errorColor: '#F43F5E',
        bodyColor: '#0B0F19',
        cardColor: '#141C2E',
        modalColor: '#141C2E',
        popoverColor: '#182238',
        tableColor: '#141C2E',
        tableHeaderColor: '#182238',
        inputColor: '#182238',
        actionColor: '#182238',
        borderColor: 'rgba(148,163,184,.18)',
        dividerColor: 'rgba(148,163,184,.12)',
        textColorBase: '#F8FAFC',
        textColor1: '#F8FAFC',
        textColor2: '#94A3B8',
        textColor3: '#64748B',
        borderRadius: '9px',
        borderRadiusSmall: '6px',
        fontFamily: '"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif',
        fontSize: '14px',
        fontSizeMedium: '14px'
    },
    Menu: {
        itemColorActive: 'rgba(14,165,233,.12)',
        itemTextColorActive: '#38BDF8',
        itemTextColorActiveHover: '#38BDF8',
        itemColorActiveHover: 'rgba(14,165,233,.08)'
    },
    Button: {
        iconSizeTiny: '11px',
        iconSizeSmall: '12px',
        iconSizeMedium: '13px',
        iconSizeLarge: '15px',
        iconMarginSmall: '5px',
        iconMarginMedium: '6px',
        fontWeight: '500'
    },
    DataTable: {
        thColor: '#182238',
        tdColor: '#141C2E',
        thTextColor: '#94A3B8',
        borderColor: 'rgba(148,163,184,.12)'
    },
    Card: {
        borderColor: 'rgba(148,163,184,.12)'
    }
}

