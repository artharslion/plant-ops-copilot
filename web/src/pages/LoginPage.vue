<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useSession } from '../stores/session'
const session = useSession()
const router = useRouter()
const userName = ref('')
const password = ref('')
const busy = ref(false)
const error = ref('')
async function login() {
  busy.value = true; error.value = ''
  try { await session.login(userName.value, password.value); password.value = ''; await router.push('/assets') }
  catch (e) { error.value = e instanceof Error ? e.message : '登录失败。'; password.value = '' }
  finally { busy.value = false }
}
</script>
<template>
  <header class="topbar"><span>工作空间 / 登录</span><span class="stage-chip">ASSET DIRECTORY</span></header>
  <section class="login-layout">
    <div><p class="eyebrow">PLANT OPERATIONS / 01</p><h1>每一次行动，<br />从了解资产开始。</h1><p class="intro">登录你的工作空间，浏览授权工厂的设备目录。资产修改由角色与工厂授权共同决定。</p><div class="login-path">工厂 <span>→</span> 位置 <span>→</span> 设备</div><p class="path-note">本地演示使用虚构工厂与设备。</p></div>
    <form class="login-card" @submit.prevent="login">
      <p class="eyebrow">LOCAL ACCOUNT</p><h2>登录工作台</h2>
      <label>用户名<input v-model="userName" name="username" autocomplete="username" required maxlength="256" /></label>
      <label>密码<input v-model="password" name="password" type="password" autocomplete="current-password" required maxlength="1024" /></label>
      <p v-if="error || session.error" class="error-banner" role="alert">{{ error || session.error }}</p>
      <button :disabled="busy">{{ busy ? '正在登录…' : '登录' }}</button>
      <p class="path-note">演示账号与密码请使用本地配置中的信息。</p>
    </form>
  </section>
</template>
