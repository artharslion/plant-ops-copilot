<script setup lang="ts">
import { locale, messageRef, setLocale, t } from './i18n'
import { computed, ref, watch } from 'vue'
import { ElConfigProvider } from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import en from 'element-plus/es/locale/lang/en'
import { useRouter } from 'vue-router'
import { useSession } from './stores/session'
import WorkOrderDrawer from './components/WorkOrderDrawer.vue'
const session = useSession()
const router = useRouter()
const error = messageRef()
const busy = ref(false)
const componentLocale = computed(() => (locale.value === 'en' ? en : zhCn))
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
    error.value = () => (e instanceof Error ? e.message : t('退出失败。'))
  } finally {
    busy.value = false
  }
}
</script>
<template>
  <ElConfigProvider :locale="componentLocale">
    <div class="app-shell">
      <aside class="sidebar">
        <a class="brand" href="/" :aria-label="t('Plant Ops Copilot 首页')"
          ><span class="brand-mark">PO</span><span>Plant Ops<br /><small>Copilot</small></span></a
        >
        <div class="language-switcher">
          <label for="interface-language">{{ t('界面语言') }}</label>
          <select
            id="interface-language"
            :value="locale"
            @change="setLocale(($event.target as HTMLSelectElement).value)"
          >
            <option value="zh-CN" lang="zh-CN">简体中文</option>
            <option value="en" lang="en">English</option>
          </select>
        </div>
        <div class="nav-caption">{{ t('工作空间') }}</div>
        <RouterLink class="nav-item" to="/">{{ t('总览') }} <span>01</span></RouterLink>
        <RouterLink class="nav-item" to="/assets">{{ t('资产目录') }} <span>02</span></RouterLink>
        <RouterLink class="nav-item" to="/alarms">{{ t('告警台') }} <span>03</span></RouterLink>
        <RouterLink class="nav-item" to="/documents"
          >{{ t('知识资料') }} <span>04</span></RouterLink
        >
        <RouterLink v-if="session.user?.roles.includes('Admin')" class="nav-item" to="/settings"
          >{{ t('设置') }} <span>05</span></RouterLink
        >
        <RouterLink v-if="!session.user" class="nav-item" to="/login"
          >{{ t('登录') }} <span>→</span></RouterLink
        >
        <div v-else class="session-panel">
          <p>{{ session.user.displayName }}</p>
          <small>{{ session.user.roles.join(' / ') }}</small
          ><el-button :disabled="busy" @click="logout">{{ t('退出登录') }}</el-button>
          <p v-if="error" role="alert">{{ error }}</p>
        </div>
        <p class="sidebar-note">
          {{ t('资产、告警与维护先行。') }}<br />{{ t('AI 提供有证据的辅助调查。') }}<br />{{
            realtimeStatus
          }}
        </p>
        <div class="sidebar-footer">
          LOCAL DEVELOPMENT<br /><span>{{ t('资产、告警、维护与资料 · P1.7') }}</span>
        </div>
      </aside>
      <main><RouterView /></main>
      <WorkOrderDrawer />
    </div>
  </ElConfigProvider>
</template>
