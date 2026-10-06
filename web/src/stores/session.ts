import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { ApiError, request, type CurrentUser } from '../api'

export const useSession = defineStore('session', () => {
  const user = ref<CurrentUser | null>(null)
  const error = ref('')
  const admin = computed(() => user.value?.roles.includes('Admin') ?? false)
  async function load() {
    try {
      user.value = await request<CurrentUser>('/auth/me')
      error.value = ''
    } catch (e) {
      user.value = null
      if (e instanceof ApiError && e.status === 401) return
      error.value = e instanceof Error ? e.message : '无法获取当前用户。'
      throw e
    }
  }
  async function login(userName: string, password: string) {
    await request('/auth/login', 'POST', { userName, password })
    await load()
  }
  async function logout() {
    await request('/auth/logout', 'POST')
    user.value = null
  }
  return { user, error, admin, load, login, logout }
})
