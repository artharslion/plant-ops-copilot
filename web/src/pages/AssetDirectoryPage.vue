<script setup lang="ts">
import { messageRef, t } from '../i18n'
import { formatDateTime as at } from '../time'
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { ApiError, listAll, request, type DirectoryRecord } from '../api'
import { useSession } from '../stores/session'
import { useRoute } from 'vue-router'
import { useWorkOrderDrawer } from '../stores/workOrderDrawer'
import SidePanel from '../components/SidePanel.vue'
import DirectoryEditor from '../components/DirectoryEditor.vue'
import MeasurementPanel from '../components/MeasurementPanel.vue'
import WorkOrderPanel from '../components/WorkOrderPanel.vue'
import DocumentPanel from '../components/DocumentPanel.vue'
const editorPanel = ref<InstanceType<typeof SidePanel>>()
const session = useSession()
const route = useRoute()
const workOrderDrawer = useWorkOrderDrawer()
const plants = ref<DirectoryRecord[]>([])
const locations = ref<DirectoryRecord[]>([])
const assets = ref<DirectoryRecord[]>([])
const plantId = ref('')
const locationId = ref('')
const asset = ref<DirectoryRecord | null>(null)
const loading = ref(false)
const saving = ref(false)
const error = messageRef()
const notice = messageRef()
const expired = ref(false)
const editor = ref<{ kind: string; record: DirectoryRecord | null } | null>(null)
const assetTab = ref('overview')
const assetTabs = computed(() => [
  { id: 'overview', label: t('设备概览') },
  { id: 'measurements', label: t('测量与趋势') },
  { id: 'orders', label: t('维护工单') },
  { id: 'history', label: t('维修历史') },
  { id: 'documents', label: t('设备资料') },
])
const plant = computed(() => plants.value.find((x) => x.id === plantId.value))
const location = computed(() => locations.value.find((x) => x.id === locationId.value))
const visibleAssets = computed(() =>
  assets.value.filter((x) => !locationId.value || x.locationId === locationId.value),
)
type LocationNode = { id: string; label: string; children: LocationNode[] }
const tree = computed(() => {
  function children(parent: string | null): LocationNode[] {
    return locations.value
      .filter((x) => (x.parentLocationId ?? null) === parent)
      .sort(
        (a, b) =>
          Number(a.sortOrder ?? 0) - Number(b.sortOrder ?? 0) || a.code.localeCompare(b.code),
      )
      .map((x) => ({
        id: x.id,
        label: x.name + (x.isArchived ? t('（已归档）') : ''),
        children: children(x.id),
      }))
  }
  return [{ id: '', label: t('全部位置（{0}）', [assets.value.length]), children: children(null) }]
})
function fail(e: unknown) {
  error.value = () => (e instanceof Error ? e.message : t('操作失败。'))
  expired.value = e instanceof ApiError && e.status === 401
}
async function loadPlant() {
  loading.value = true
  error.value = ''
  asset.value = null
  locations.value = []
  assets.value = []
  editor.value = null
  try {
    if (plantId.value)
      [locations.value, assets.value] = await Promise.all([
        listAll(`/plants/${plantId.value}/locations`),
        listAll(`/assets?plantId=${plantId.value}`),
      ])
    if (!locations.value.some((x) => x.id === locationId.value)) locationId.value = ''
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}
async function refresh() {
  loading.value = true
  error.value = ''
  notice.value = ''
  plants.value = []
  locations.value = []
  assets.value = []
  asset.value = null
  editor.value = null
  try {
    await session.load()
    if (!session.user) throw new ApiError(401, t('会话已失效，请重新登录。'))
    plants.value = await listAll('/plants')
    if (!plants.value.some((x) => x.id === plantId.value))
      plantId.value = plants.value.find((x) => !x.isArchived)?.id ?? plants.value[0]?.id ?? ''
    await loadPlant()
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}
async function selectAsset(id: string) {
  loading.value = true
  error.value = ''
  editor.value = null
  asset.value = null
  try {
    const selected = await request<DirectoryRecord>(`/assets/${id}`)
    if (selected.plantId !== plantId.value) {
      plantId.value = selected.plantId
      locationId.value = ''
      await loadPlant()
    }
    assetTab.value =
      typeof route.query.tab === 'string' &&
      assetTabs.value.some((tab) => tab.id === route.query.tab)
        ? route.query.tab
        : typeof route.query.alarmId === 'string'
          ? 'orders'
          : 'overview'
    asset.value = selected
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}
function edit(kind: string, record: DirectoryRecord | null) {
  error.value = ''
  notice.value = ''
  editor.value = { kind, record }
}
function changePlant() {
  locationId.value = ''
  void loadPlant()
}
function selectLocation(id: string) {
  locationId.value = id
  asset.value = null
  editor.value = null
}
async function save(input: object) {
  if (!editor.value) return
  saving.value = true
  error.value = ''
  notice.value = ''
  const { kind, record } = editor.value
  try {
    const path = record
      ? `/${kind}/${record.id}`
      : kind === 'plants'
        ? '/plants'
        : `/plants/${plantId.value}/${kind}`
    const saved = await request<DirectoryRecord>(path, record ? 'PATCH' : 'POST', input)
    if (kind === 'plants') plantId.value = saved.id
    await refresh()
    if (kind === 'assets') await selectAsset(saved.id)
    if (kind === 'locations') locationId.value = saved.id
    notice.value = () => t('已保存 {0} · 版本 {1} · 已记录审计', [saved.name, saved.version])
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}
async function openLinkedWorkOrder() {
  if (typeof route.query.workOrderId !== 'string' || typeof route.query.assetId !== 'string') return
  if (asset.value?.id !== route.query.assetId) await selectAsset(route.query.assetId)
  if (asset.value?.id !== route.query.assetId) return
  await nextTick()
  workOrderDrawer.open({
    assetId: asset.value.id,
    orderId: route.query.workOrderId,
    alarmId: typeof route.query.alarmId === 'string' ? route.query.alarmId : undefined,
  })
}
watch(
  () => route.query.workOrderId,
  () => {
    void openLinkedWorkOrder()
  },
)
onMounted(async () => {
  await refresh()
  if (typeof route.query.assetId === 'string') await selectAsset(route.query.assetId)
  await openLinkedWorkOrder()
})
</script>
<template>
  <header class="topbar">
    <span>{{ t('工作空间 / 资产目录') }}</span
    ><span class="stage-chip">{{ session.user?.roles.join(' / ') }}</span>
  </header>
  <section class="page-heading">
    <div>
      <p class="eyebrow">ASSET DIRECTORY / 01</p>
      <h1>{{ t('了解工厂中的每一台设备。') }}</h1>
      <p class="intro">
        {{
          session.admin
            ? t('管理授权工厂内的资产。每一次保存都检查版本并记录审计。')
            : t('浏览授权工厂的设备目录。当前角色可读取资产信息。')
        }}
      </p>
    </div>
    <el-button :disabled="loading || saving" @click="refresh" type="primary">{{
      t('刷新目录')
    }}</el-button>
  </section>
  <p v-if="error" class="error-banner" role="alert">
    {{ error }} <RouterLink v-if="expired" to="/login">{{ t('重新登录') }}</RouterLink>
  </p>
  <el-alert v-if="notice" :title="notice" type="success" :closable="false" show-icon />
  <div class="directory-toolbar">
    <el-form-item :label="t('工厂')"
      ><el-select filterable v-model="plantId" :disabled="loading || saving" @change="changePlant">
        <el-option v-if="!plants.length" value="" :label="t('无授权工厂')" />
        <el-option
          v-for="p in plants"
          :key="p.id"
          :value="p.id"
          :label="p.name + (p.isArchived ? t('（已归档）') : '')"
        /> </el-select
    ></el-form-item>
    <div v-if="session.admin" class="actions">
      <el-button :disabled="loading || saving" @click="edit('plants', null)">
        {{ t('新增工厂') }}</el-button
      ><el-button :disabled="!plant || loading || saving" @click="edit('plants', plant ?? null)">
        {{ t('编辑工厂') }}
      </el-button>
    </div>
  </div>
  <section v-if="plant" class="directory-grid" :aria-busy="loading">
    <aside class="location-panel">
      <div class="section-title">
        <h2>{{ t('位置树') }}</h2>
        <el-button
          v-if="session.admin && !plant.isArchived"
          class="small-button"
          :disabled="loading || saving"
          @click="edit('locations', null)"
          type="primary"
        >
          {{ t('新增位置') }}
        </el-button>
      </div>
      <el-tree
        :data="tree"
        node-key="id"
        default-expand-all
        highlight-current
        :current-node-key="locationId"
        :expand-on-click-node="false"
        :aria-label="t('工厂位置')"
        @node-click="(node: LocationNode) => !loading && !saving && selectLocation(node.id)"
      />
      <p v-if="!locations.length" class="path-note">
        {{ loading ? t('正在加载位置…') : t('暂无位置。') }}
      </p>
      <el-button
        v-if="session.admin && location && !plant.isArchived"
        :disabled="loading || saving"
        @click="edit('locations', location ?? null)"
      >
        {{ t('编辑所选位置') }}
      </el-button>
    </aside>
    <div class="asset-list-panel">
      <div class="section-title">
        <div>
          <p class="eyebrow">{{ plant.code }} / {{ location?.code ?? 'ALL LOCATIONS' }}</p>
          <h2>{{ location?.name ?? t('全部设备') }}</h2>
        </div>
        <el-button
          v-if="session.admin && !plant.isArchived"
          class="small-button"
          :disabled="loading || saving"
          @click="edit('assets', null)"
          type="primary"
        >
          {{ t('新增设备') }}
        </el-button>
      </div>
      <el-table
        :data="visibleAssets"
        v-loading="loading"
        row-key="id"
        highlight-current-row
        :empty-text="t('此位置暂无设备')"
        @row-click="(item: DirectoryRecord) => !saving && selectAsset(item.id)"
      >
        <el-table-column prop="code" :label="t('设备代码')" min-width="150"
          ><template #default="{ row }"
            ><el-button
              link
              type="primary"
              :disabled="loading || saving"
              @click.stop="selectAsset(row.id)"
              >{{ row.code }}</el-button
            ></template
          ></el-table-column
        >
        <el-table-column prop="name" :label="t('设备名称')" min-width="180" />
        <el-table-column :label="t('生命周期')" min-width="140"
          ><template #default="{ row }"
            ><el-tag :type="row.isArchived ? 'info' : 'success'">{{
              row.isArchived ? t('已归档') : row.lifecycleStatus
            }}</el-tag></template
          ></el-table-column
        >
        <el-table-column prop="version" :label="t('版本')" width="80" />
      </el-table>
    </div>
  </section>
  <SidePanel
    v-if="asset"
    :title="asset.name"
    :subtitle="`${plant?.name} / ${asset.code}`"
    @close="asset = null"
  >
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <el-alert v-if="notice" :title="notice" type="success" :closable="false" show-icon />
    <el-tabs v-model="assetTab" :aria-label="t('设备详情分区')">
      <el-tab-pane name="overview" :label="t('设备概览')">
        <section v-show="assetTab === 'overview'" class="asset-detail">
          <div class="section-title">
            <div>
              <p class="eyebrow">
                {{ plant?.name }} → {{ locations.find((x) => x.id === asset?.locationId)?.name }} →
                {{ asset.code }}
              </p>
              <h2>{{ asset.name }}</h2>
            </div>
            <el-button
              v-if="session.admin && !plant?.isArchived"
              :disabled="loading || saving"
              @click="edit('assets', asset)"
              type="primary"
            >
              {{ t('编辑设备') }}
            </el-button>
          </div>
          <dl class="detail-grid">
            <div>
              <dt>{{ t('设备类型') }}</dt>
              <dd>{{ asset.assetType }}</dd>
            </div>
            <div>
              <dt>{{ t('生命周期') }}</dt>
              <dd>{{ asset.lifecycleStatus }}</dd>
            </div>
            <div>
              <dt>{{ t('重要程度') }}</dt>
              <dd>{{ asset.criticality }}</dd>
            </div>
            <div>
              <dt>{{ t('版本') }}</dt>
              <dd>{{ asset.version }}</dd>
            </div>
            <div>
              <dt>{{ t('制造商 / 型号') }}</dt>
              <dd>{{ asset.manufacturer || t('未填写') }} / {{ asset.model || t('未填写') }}</dd>
            </div>
            <div>
              <dt>{{ t('序列号') }}</dt>
              <dd>{{ asset.serialNumber || t('未填写') }}</dd>
            </div>
            <div>
              <dt>{{ t('安装时间') }}</dt>
              <dd>
                {{ asset.installedAt ? at(asset.installedAt) : t('未填写') }}
              </dd>
            </div>
            <div>
              <dt>{{ t('归档') }}</dt>
              <dd>{{ asset.isArchived ? t('已归档，历史保留') : t('未归档') }}</dd>
            </div>
          </dl>
          <p class="path-note">
            <RouterLink
              :to="{ path: '/alarms', query: { plantId: asset.plantId, assetId: asset.id } }"
              >{{ t('查看此设备的告警 →') }}</RouterLink
            >
          </p>
        </section>
      </el-tab-pane>
      <el-tab-pane name="measurements" :label="t('测量与趋势')">
        <MeasurementPanel
          v-if="assetTab === 'measurements'"
          :key="asset.id"
          :asset-id="asset.id"
          :initial-point-id="
            typeof route.query.pointId === 'string' ? route.query.pointId : undefined
          "
          :plant-id="asset.plantId"
          :admin="session.admin"
          :archived="asset.isArchived || !!plant?.isArchived"
      /></el-tab-pane>
      <el-tab-pane name="orders" :label="t('维护工单')">
        <WorkOrderPanel
          v-show="assetTab === 'orders'"
          :active="assetTab === 'orders'"
          :key="`work-${asset.id}`"
          :initial-alarm-id="
            typeof route.query.alarmId === 'string' ? route.query.alarmId : undefined
          "
          :asset-id="asset.id"
          :plant-id="asset.plantId"
          :archived="asset.isArchived || !!plant?.isArchived"
      /></el-tab-pane>
      <el-tab-pane name="history" :label="t('维修历史')">
        <WorkOrderPanel
          v-show="assetTab === 'history'"
          :active="assetTab === 'history'"
          :key="`history-${asset.id}`"
          :asset-id="asset.id"
          :plant-id="asset.plantId"
          history
      /></el-tab-pane>
      <el-tab-pane name="documents" :label="t('设备资料')">
        <DocumentPanel
          v-show="assetTab === 'documents'"
          :active="assetTab === 'documents'"
          :key="`docs-${asset.id}`"
          :plant-id="asset.plantId"
          :asset-id="asset.id"
          :archived="asset.isArchived || !!plant?.isArchived"
        />
      </el-tab-pane>
    </el-tabs>
  </SidePanel>
  <SidePanel
    ref="editorPanel"
    v-if="editor"
    :title="t('目录编辑')"
    mode="dialog"
    :level="asset ? 1 : 0"
    :busy="saving"
    dirty
    @close="editor = null"
  >
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <DirectoryEditor
      v-if="editor"
      :key="`${editor.kind}-${editor.record?.id ?? 'new'}`"
      :kind="editor.kind"
      :record="editor.record"
      :locations="locations"
      :busy="saving"
      @save="save"
      @cancel="editorPanel?.requestClose()"
    />
  </SidePanel>
  <p v-if="!plant && !loading && !error" class="path-note">{{ t('当前账号没有工厂授权。') }}</p>
</template>
