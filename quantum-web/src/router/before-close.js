import { dialog } from '@/libs/naive-discrete'

const beforeClose = {
    before_close_normal: (resolve) => {
        dialog.warning({
            title: '提示',
            content: '确定要关闭这一页吗',
            positiveText: '确定',
            negativeText: '取消',
            onPositiveClick: () => {
                resolve(true)
            },
            onNegativeClick: () => {
                resolve(false)
            }
        })
    }
}

export default beforeClose
