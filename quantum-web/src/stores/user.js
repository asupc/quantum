import { defineStore } from 'pinia'
import { login } from '@/api/user'
import { getToken } from '@/libs/util'

export const useUserStore = defineStore('user', {
    state: () => ({
        userName: '',
        userId: '',
        avatarImgPath: '',
        token: getToken(),
        access: '',
        hasGetInfo: false,
        unreadCount: 0
    }),
    actions: {
        // 登录（返回后端 token，登录态由 localStorage.accessToken 承载）
        handleLogin({ userName, password }) {
            userName = userName.trim()
            return login({ userName, password })
        }
    }
})
