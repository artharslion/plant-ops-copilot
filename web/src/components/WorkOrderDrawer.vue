<script setup lang="ts">
import { computed, nextTick, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { request, type DirectoryRecord } from '../api'
import { useSession } from '../stores/session'
import { useWorkOrderDrawer } from '../stores/workOrderDrawer'
import SidePanel from './SidePanel.vue'
import WorkOrderPanel from './WorkOrderPanel.vue'

const drawer = useWorkOrderDrawer()
const session = useSession()
const route = useRoute()
const router = useRouter()
const panel = ref<InstanceType<typeof WorkOrderPanel>>()
const asset = ref<DirectoryRecord | null>(null)
const plant = ref<DirectoryRecord | null>(null)
const loading = ref(false),
  error = ref('')
const busy = computed(() => loading.value || !!panel.value?.busy)
let generation = 0
watch(
  () => route.fullPath,
  () => {
    // AssetDirectoryPage opens a legacy deep link only after its underlying asset drawer is mounted.
    if (
      route.path !== '/assets' ||
      typeof route.query.workOrderId !== 'string' ||
      typeof route.query.assetId !== 'string'
    )
      drawer.close()
  },
  { immediate: true },
)
watch(
  () => session.user?.id,
  (id) => {
    if (!id) drawer.close()
  },
)
watch(
  () => drawer.target,
  async (target) => {
    const current = ++generation
    asset.value = null
    plant.value = null
    error.value = ''
    loading.value = !!target
    if (!target) return
    try {
      const selected = await request<DirectoryRecord>(`/assets/${target.assetId}`)
      const selectedPlant = await request<DirectoryRecord>(`/plants/${selected.plantId}`)
      if (current !== generation) return
      asset.value = selected
      plant.value = selectedPlant
    } catch (e) {
      if (current === generation)
        error.value = e instanceof Error ? e.message : '读取工单设备失败。'
    } finally {
      if (current === generation) loading.value = false
    }
  },
  { immediate: true },
)
async function close() {
  drawer.close()
  if (route.query.workOrderId) {
    const query = { ...route.query }
    delete query.workOrderId
    // Unmount the discarded form/guard before cleaning a legacy deep link's query.
    await nextTick()
    await router.replace({ path: route.path, query })
  }
}
async function created(orderId: string) {
  if (!drawer.target) return
  if (route.query.workOrderId) {
    await router.replace({ path: route.path, query: { ...route.query, workOrderId: orderId } })
  } else drawer.open({ ...drawer.target, orderId })
}
onUnmounted(() => {
  generation++
})
</script>
<template>
  <SidePanel
    v-if="drawer.target"
    title="维护工单"
    :subtitle="asset ? `${asset.code} · ${asset.name}` : '正在读取设备'"
    size="min(960px, calc(100vw - 24px))"
    :busy="busy"
    :dirty="panel?.dirty"
    global-route-protection
    @close="close"
  >
    <el-alert v-if="error" :title="error" type="error" :closable="false" />
    <el-skeleton v-else-if="loading" :rows="8" animated />
    <WorkOrderPanel
      v-else-if="asset"
      ref="panel"
      :key="`${drawer.target.assetId}:${drawer.target.orderId}`"
      :asset-id="asset.id"
      :plant-id="asset.plantId"
      :archived="asset.isArchived || plant?.isArchived"
      :asset-label="`${asset.code} · ${asset.name}`"
      :detail-id="drawer.target.orderId"
      :initial-alarm-id="drawer.target.alarmId"
      embedded
      @created="created"
    />
  </SidePanel>
</template>
