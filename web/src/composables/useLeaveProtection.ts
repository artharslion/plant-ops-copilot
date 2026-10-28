import { t } from '../i18n'
import { onBeforeUnmount, onMounted } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRouter } from 'vue-router'
import { ElMessageBox } from 'element-plus'

export function useLeaveProtection(dirty: () => boolean, busy: () => boolean, global = false) {
  async function canLeave() {
    if (busy()) return false
    if (!dirty()) return true
    try {
      await ElMessageBox.confirm(t('未保存的内容将丢失。'), t('离开当前页面？'), {
        confirmButtonText: t('放弃并离开'),
        cancelButtonText: t('继续编辑'),
        type: 'warning',
      })
      return true
    } catch {
      return false
    }
  }
  function beforeUnload(event: BeforeUnloadEvent) {
    if (dirty() || busy()) {
      event.preventDefault()
      event.returnValue = ''
    }
  }
  if (global) {
    // App-level drawers sit outside RouterView and therefore need a removable global guard.
    const removeGuard = useRouter().beforeEach(canLeave)
    onBeforeUnmount(removeGuard)
  } else {
    onBeforeRouteLeave(canLeave)
    onBeforeRouteUpdate(canLeave)
  }
  onMounted(() => window.addEventListener('beforeunload', beforeUnload))
  onBeforeUnmount(() => window.removeEventListener('beforeunload', beforeUnload))
  return canLeave
}
