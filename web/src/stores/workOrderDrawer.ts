import { defineStore } from 'pinia'
import { ref } from 'vue'

export type WorkOrderTarget = { assetId: string; orderId: string; alarmId?: string }

export const useWorkOrderDrawer = defineStore('workOrderDrawer', () => {
  const target = ref<WorkOrderTarget | null>(null)
  function open(value: WorkOrderTarget) {
    target.value = { ...value }
  }
  function close() {
    target.value = null
  }
  return { target, open, close }
})
