<script setup lang="ts">
import { messageRef, t } from '../i18n'
import { onMounted, ref } from 'vue'
import { request } from '../api'
import { useSession } from '../stores/session'

interface AiSettings {
  provider: 'Fake' | 'OpenAiCompatible'
  realModel: string
  realConfigured: boolean
  version: number
}
const session = useSession()
const settings = ref<AiSettings>()
const selected = ref<AiSettings['provider']>('Fake')
const loading = ref(false)
const saving = ref(false)
const error = messageRef()
const saved = messageRef()

async function load() {
  loading.value = true
  error.value = ''
  saved.value = ''
  try {
    settings.value = await request<AiSettings>('/settings/ai')
    selected.value = settings.value.provider
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('AI 设置读取失败。'))
  } finally {
    loading.value = false
  }
}
async function save() {
  if (!settings.value || saving.value) return
  saving.value = true
  error.value = ''
  saved.value = ''
  try {
    settings.value = await request<AiSettings>('/settings/ai', 'PUT', {
      provider: selected.value,
      version: settings.value.version,
    })
    selected.value = settings.value.provider
    saved.value = () => t('已保存。之后新建的调查将使用此模式。')
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('保存失败，请刷新核对当前设置。'))
  } finally {
    saving.value = false
  }
}
onMounted(() => {
  if (session.user?.roles.includes('Admin')) void load()
})
</script>

<template>
  <header class="topbar">
    <span>{{ t('工作空间 / 设置') }}</span>
  </header>
  <section class="page-heading">
    <div>
      <p class="eyebrow">SETTINGS / 05</p>
      <h1>{{ t('AI 调查设置') }}</h1>
      <p class="intro">{{ t('为整个工作台选择调查模式。采集和告警不会自动调用模型。') }}</p>
    </div>
  </section>
  <section class="ai-settings-card" aria-labelledby="ai-settings-title">
    <h2 id="ai-settings-title">{{ t('模型调用方式') }}</h2>
    <p v-if="!session.user?.roles.includes('Admin')" role="alert">
      {{ t('仅管理员可管理 AI 设置。') }}
    </p>
    <template v-else>
      <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
      <el-skeleton v-if="loading" :rows="3" animated />
      <template v-else-if="settings">
        <p>
          {{ t('当前模式：')
          }}<el-tag :type="settings.provider === 'Fake' ? 'info' : 'success'">
            {{ settings.provider === 'Fake' ? t('模拟 AI') : t('真实 LLM') }}
          </el-tag>
        </p>
        <p class="muted">
          {{
            settings.realConfigured
              ? t('服务端已配置模型：{0}', [settings.realModel])
              : t('服务端尚未配置真实模型连接信息。')
          }}
          {{ t('密钥由服务端保管，不会显示在页面中。') }}
        </p>
        <el-form label-position="top" @submit.prevent="save">
          <el-form-item :label="t('新调查使用的模式')">
            <el-select
              v-model="selected"
              :aria-label="t('新调查使用的模式')"
              :disabled="saving"
              @change="saved = ''"
            >
              <el-option value="Fake" :label="t('模拟 AI · 不请求外部模型')" />
              <el-option
                value="OpenAiCompatible"
                :label="t('真实 LLM · 使用服务端配置')"
                :disabled="!settings.realConfigured"
              />
            </el-select>
          </el-form-item>
          <p class="muted">
            {{ t('切换仅影响之后新建的调查。已排队或运行中的调查继续使用创建时的模式。') }}
          </p>
          <el-alert
            v-if="selected === 'OpenAiCompatible'"
            :title="
              t(
                '保存设置不会请求模型。手动发起新调查时会调用真实服务，可能产生费用；服务已配置不代表当前连接一定可用。',
              )
            "
            type="info"
            :closable="false"
            show-icon
          />
          <div class="ai-settings-actions">
            <el-button
              type="primary"
              native-type="submit"
              :loading="saving"
              :disabled="loading || saving || selected === settings.provider"
              >{{ t('保存设置') }}</el-button
            >
            <el-button :disabled="saving || loading" @click="load">{{ t('刷新设置') }}</el-button>
          </div>
        </el-form>
        <p v-if="saved" role="status">{{ saved }}</p>
      </template>
      <el-button v-else :disabled="loading" @click="load">{{ t('重新读取') }}</el-button>
    </template>
  </section>
</template>

<style scoped>
.ai-settings-card {
  max-width: 760px;
  padding: 28px;
  background: #fff;
  border: 1px solid #dce2e7;
  border-radius: 12px;
}
.ai-settings-actions {
  display: flex;
  gap: 12px;
  margin-top: 24px;
}
.muted {
  color: #526171;
  line-height: 1.7;
}
</style>
