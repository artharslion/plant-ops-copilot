<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useSession } from './stores/session'
const session = useSession()
const router = useRouter()
const error = ref('')
const busy = ref(false)
import { realtimeStatus, startRealtime, stopRealtime } from './realtime'
watch(
  () => session.user?.id,
  (id) => {
    if (id) startRealtime()
    else void stopRealtime()
  },
  { immediate: true },
)
async function logout() {
  busy.value = true
  error.value = ''
  try {
    await session.logout()
    await router.push('/login')
  } catch (e) {
    error.value = e instanceof Error ? e.message : '退出失败。'
  } finally {
    busy.value = false
  }
}
</script>
<template>
  <div class="app-shell">
    <aside class="sidebar">
      <a class="brand" href="/" aria-label="Plant Ops Copilot 首页"
        ><span class="brand-mark">PO</span><span>Plant Ops<br /><small>Copilot</small></span></a
      >
      <div class="nav-caption">工作空间</div>
      <RouterLink class="nav-item" to="/">总览 <span>01</span></RouterLink>
      <RouterLink class="nav-item" to="/assets">资产目录 <span>02</span></RouterLink>
      <RouterLink class="nav-item" to="/alarms">告警台 <span>03</span></RouterLink>
      <RouterLink class="nav-item" to="/documents">知识资料 <span>04</span></RouterLink>
      <RouterLink v-if="!session.user" class="nav-item" to="/login">登录 <span>→</span></RouterLink>
      <div v-else class="session-panel">
        <p>{{ session.user.displayName }}</p>
        <small>{{ session.user.roles.join(' / ') }}</small
        ><button class="secondary" :disabled="busy" @click="logout">退出登录</button>
        <p v-if="error" role="alert">{{ error }}</p>
      </div>
      <p class="sidebar-note">
        资产、告警与维护先行。<br />AI 提供有证据的辅助调查。<br />{{ realtimeStatus }}
      </p>
      <div class="sidebar-footer">
        LOCAL DEVELOPMENT<br /><span>资产、告警、维护与资料 · P1.7</span>
      </div>
    </aside>
    <main><RouterView /></main>
  </div>
</template>
