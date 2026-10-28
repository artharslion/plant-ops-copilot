<script setup lang="ts">
import { t } from '../i18n'
import { ref } from 'vue'
import { useLeaveProtection } from '../composables/useLeaveProtection'
const props = defineProps<{
  title: string
  subtitle?: string
  busy?: boolean
  dirty?: boolean
  level?: number
  mode?: 'drawer' | 'dialog'
  size?: string
  globalRouteProtection?: boolean
}>()
const emit = defineEmits<{ close: [] }>()
useLeaveProtection(
  () => !!props.dirty,
  () => !!props.busy,
  props.globalRouteProtection,
)
const confirmClose = ref(false)
function close() {
  if (props.busy) return
  if (props.dirty) confirmClose.value = true
  else emit('close')
}
function discardAndClose() {
  if (props.busy) return
  confirmClose.value = false
  emit('close')
}
defineExpose({ requestClose: close })
</script>
<template>
  <component
    :is="mode === 'dialog' ? 'el-dialog' : 'el-drawer'"
    :model-value="true"
    :title="title"
    :size="size ?? 'min(1120px, 100vw)'"
    width="min(760px, calc(100vw - 24px))"
    class="resource-panel"
    append-to-body
    :close-on-click-modal="false"
    :before-close="close"
  >
    <p v-if="subtitle" class="eyebrow">{{ subtitle }}</p>
    <slot />
    <el-dialog
      v-model="confirmClose"
      :title="t('放弃未保存的内容？')"
      width="min(420px, calc(100vw - 24px))"
      append-to-body
      :close-on-click-modal="false"
    >
      <p>{{ t('有尚未保存的内容，关闭后将丢失。') }}</p>
      <template #footer>
        <el-button @click="confirmClose = false">{{ t('继续编辑') }}</el-button>
        <el-button type="danger" :disabled="busy" @click="discardAndClose">{{
          t('放弃并关闭')
        }}</el-button>
      </template>
    </el-dialog>
  </component>
</template>
