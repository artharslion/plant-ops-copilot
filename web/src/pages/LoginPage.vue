<script setup lang="ts">
import { messageRef, t } from '../i18n'
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useSession } from '../stores/session'
const session = useSession()
const router = useRouter()
const userName = ref('')
const password = ref('')
const busy = ref(false)
const error = messageRef()
async function login() {
  busy.value = true
  error.value = ''
  try {
    await session.login(userName.value, password.value)
    password.value = ''
    await router.push('/assets')
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('登录失败。'))
    password.value = ''
  } finally {
    busy.value = false
  }
}
</script>
<template>
  <header class="topbar">
    <span>{{ t('工作空间 / 登录') }}</span
    ><span class="stage-chip">ASSET DIRECTORY</span>
  </header>
  <section class="login-layout">
    <div>
      <p class="eyebrow">PLANT OPERATIONS / 01</p>
      <h1>{{ t('每一次行动，') }}<br />{{ t('从了解资产开始。') }}</h1>
      <p class="intro">
        {{ t('登录你的工作空间，浏览授权工厂的设备目录。资产修改由角色与工厂授权共同决定。') }}
      </p>
      <div class="login-path">
        {{ t('工厂') }} <span>→</span> {{ t('位置') }} <span>→</span> {{ t('设备') }}
      </div>
      <p class="path-note">{{ t('本地演示使用虚构工厂与设备。') }}</p>
    </div>
    <el-form label-position="top" class="login-card" @submit.prevent="login">
      <p class="eyebrow">LOCAL ACCOUNT</p>
      <h2>{{ t('登录工作台') }}</h2>
      <el-form-item :label="t('用户名')"
        ><el-input
          v-model="userName"
          name="username"
          autocomplete="username"
          required
          maxlength="256"
      /></el-form-item>
      <el-form-item :label="t('密码')"
        ><el-input
          v-model="password"
          name="password"
          type="password"
          autocomplete="current-password"
          required
          maxlength="1024"
      /></el-form-item>
      <p v-if="error || session.error" class="error-banner" role="alert">
        {{ error || session.error }}
      </p>
      <el-button :disabled="busy" native-type="submit" type="primary">{{
        busy ? t('正在登录…') : t('登录')
      }}</el-button>
      <p class="path-note">{{ t('演示账号与密码请使用本地配置中的信息。') }}</p>
    </el-form>
  </section>
</template>
