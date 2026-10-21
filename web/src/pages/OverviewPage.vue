<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { ApiError, listAll, request, type DirectoryRecord } from '../api'
import { useSession } from '../stores/session'
import { useWorkOrderDrawer } from '../stores/workOrderDrawer'
import { realtimeStatus, subscribePlantEvents } from '../realtime'
import type { components } from '../generated/api'

type Overview = components['schemas']['PlantOverview']
const session = useSession()
const workOrderDrawer = useWorkOrderDrawer()
const plants = ref<DirectoryRecord[]>([])
const plantId = ref('')
const overview = ref<Overview | null>(null)
const loading = ref(true)
const refreshing = ref(false)
const error = ref('')
const unacknowledgedOnly = ref(false)
const mine = ref(false)
const workOrderStatus = ref('')
const limit = ref(5)
const service = ref('检查中')
let generation = 0
let disposed = false
let lastRefresh = 0
let pending = false
const statusLabels: Record<string, string> = {
  Draft: '草稿',
  Open: '待执行',
  InProgress: '执行中',
  Completed: '待复核',
}
const severityLabels: Record<string, string> = {
  Critical: '紧急',
  High: '高',
  Medium: '中',
  Low: '低',
}
const issueLabels: Record<string, string> = {
  Disconnected: '采集未连接',
  Missing: '尚无有效观测',
  Stale: '数据陈旧',
  Untrusted: '观测质量异常',
}
const selectedPlant = computed(() => plants.value.find((plant) => plant.id === plantId.value))
const cards = computed(() => [
  {
    label: '活动告警',
    value: overview.value?.counts.activeAlarms,
    hint: '仍处于活动状态',
    target: 'alarms',
    kind: 'alarm',
  },
  {
    label: '待确认告警',
    value: overview.value?.counts.unacknowledgedAlarms,
    hint: '活动且尚未人工确认',
    target: 'alarms',
    kind: 'unacknowledged',
  },
  {
    label: '未关闭工单',
    value: overview.value?.counts.openWorkOrders,
    hint: '含草稿、待执行、执行中与待复核',
    target: 'orders',
    kind: 'order',
  },
  {
    label: '数据异常测点',
    value: overview.value?.counts.dataIssues,
    hint: '启用测点 · 未归档设备',
    target: 'data-issues',
    kind: 'data',
  },
])
function at(value?: string | null) {
  return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
}
function duration(value: string) {
  const minutes = Math.max(
    0,
    Math.floor((Date.parse(overview.value?.checkedAt ?? value) - Date.parse(value)) / 60000),
  )
  if (minutes < 1) return '不足 1 分钟'
  if (minutes < 60) return `${minutes} 分钟`
  if (minutes < 1440) return `${Math.floor(minutes / 60)} 小时 ${minutes % 60} 分钟`
  return `${Math.floor(minutes / 1440)} 天 ${Math.floor((minutes % 1440) / 60)} 小时`
}
function alarmLink(id: string) {
  return { path: '/alarms', query: { plantId: plantId.value, alarmId: id } }
}
function measurementLink(assetId: string, pointId: string) {
  return { path: '/assets', query: { assetId, pointId, tab: 'measurements' } }
}
async function checkService() {
  try {
    const response = await fetch('/health/ready', {
      signal: AbortSignal.timeout(6000),
      cache: 'no-store',
    })
    const body = await response.text()
    if (!disposed)
      service.value = response.ok && body.trim() === 'Healthy' ? '连接正常' : '服务或数据库暂不可用'
  } catch {
    if (!disposed) service.value = '服务暂不可用'
  }
}
async function refresh(background = false) {
  if (!plantId.value || !session.user || disposed) return
  if (background && refreshing.value) {
    pending = true
    return
  }
  const current = ++generation
  refreshing.value = true
  lastRefresh = Date.now()
  if (!background) {
    loading.value = true
    overview.value = null
  }
  try {
    const query = new URLSearchParams({
      unacknowledgedOnly: String(unacknowledgedOnly.value),
      mine: String(mine.value),
      limit: String(limit.value),
    })
    if (workOrderStatus.value) query.set('workOrderStatus', workOrderStatus.value)
    const result = await request<Overview>(`/plants/${plantId.value}/overview?${query}`)
    if (current !== generation || disposed) return
    overview.value = result
    error.value = ''
  } catch (e) {
    if (current !== generation || disposed) return
    overview.value = null
    error.value = e instanceof Error ? e.message : '工作台读取失败，请重试。'
    if (e instanceof ApiError && [401, 403, 404].includes(e.status)) {
      plants.value = []
      plantId.value = ''
      if (e.status === 401) session.user = null
    }
  } finally {
    if (current === generation && !disposed) {
      loading.value = false
      refreshing.value = false
    }
  }
}
async function initialize() {
  const current = ++generation
  overview.value = null
  loading.value = true
  error.value = ''
  try {
    await session.load()
    if (disposed || current !== generation || !session.user) return
    const result = await listAll('/plants')
    if (disposed || current !== generation) return
    plants.value = result
    plantId.value =
      result.find((plant) => plant.id === plantId.value)?.id ??
      result.find((plant) => !plant.isArchived)?.id ??
      result[0]?.id ??
      ''
    await refresh()
  } catch (e) {
    if (!disposed && current === generation)
      error.value = e instanceof Error ? e.message : '工厂读取失败。'
  } finally {
    if (!disposed) loading.value = false
  }
}
function changePlant() {
  unacknowledgedOnly.value = false
  mine.value = false
  workOrderStatus.value = ''
  limit.value = 5
  void refresh()
}
function refreshWorkspace() {
  void initialize()
  void checkService()
}
function expandLists(expanded: boolean) {
  limit.value = expanded ? 50 : 5
  void refresh()
}
async function reveal(target: string, kind: string) {
  if (!overview.value) return
  if (target === 'alarms') unacknowledgedOnly.value = kind === 'unacknowledged'
  if (target === 'orders') {
    mine.value = false
    workOrderStatus.value = ''
  }
  limit.value = 50
  await refresh()
  await nextTick()
  document.getElementById(target)?.focus({ preventScroll: true })
  document.getElementById(target)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}
watch(
  () => session.user?.id,
  (id, previous) => {
    if (!id || (previous && id !== previous)) {
      generation++
      overview.value = null
      plants.value = []
      plantId.value = ''
      refreshing.value = false
      loading.value = false
    }
  },
)
const unsubscribe = subscribePlantEvents((event) => {
  if (!event || event.plantId === plantId.value) pending = true
})
const timer = setInterval(() => {
  if (loading.value || document.visibilityState === 'hidden') return
  if (Date.now() - lastRefresh >= 15000 || (pending && Date.now() - lastRefresh >= 5000)) {
    pending = false
    void refresh(true)
  }
}, 1000)
onMounted(() => {
  void initialize()
  void checkService()
})
onUnmounted(() => {
  disposed = true
  generation++
  clearInterval(timer)
  unsubscribe()
})
</script>

<template>
  <header class="topbar">
    <span>工作空间 / 运维工作台</span><span class="stage-chip">OPERATIONS</span>
  </header>
  <section class="page-heading overview-heading">
    <div>
      <p class="eyebrow">PLANT OPERATIONS / 01</p>
      <h1>运维工作台</h1>
      <p class="intro">查看异常、安排维护，确认当前数据是否可信。</p>
    </div>
    <el-button type="primary" :loading="refreshing" :disabled="loading" @click="refreshWorkspace"
      >刷新工作台</el-button
    >
  </section>
  <p v-if="error" role="alert" class="error-banner">
    {{ error }} 当前业务数据暂不可用。<el-button @click="initialize()">重试</el-button>
  </p>
  <section v-if="!session.user && !loading" class="overview-welcome">
    <h2>登录后查看你的运维工作台</h2>
    <p>按授权工厂汇总告警、维护待办与数据采集异常。</p>
    <RouterLink class="directory-link" to="/login">登录工作空间 →</RouterLink>
  </section>
  <template v-else>
    <div class="overview-toolbar">
      <el-form-item label="工厂"
        ><el-select
          v-model="plantId"
          aria-label="选择工厂"
          :disabled="loading"
          @change="changePlant"
          ><el-option
            v-for="plant in plants"
            :key="plant.id"
            :value="plant.id"
            :label="plant.name + (plant.isArchived ? '（已归档）' : '')" /></el-select
      ></el-form-item>
      <span role="status">{{
        loading ? '正在读取…' : overview ? `更新于 ${at(overview.checkedAt)}` : '尚无可用快照'
      }}</span>
    </div>
    <p v-if="!loading && !plants.length && !error" class="overview-empty">
      暂无授权工厂，请联系管理员分配访问权限。
    </p>
    <p v-if="selectedPlant?.isArchived" class="overview-note">
      该工厂已归档，告警与未关闭工单仍保留展示；数据异常统计不包含已归档工厂。
    </p>
    <div class="overview-metrics" :aria-busy="loading">
      <button
        v-for="card in cards"
        :key="card.kind"
        class="overview-metric"
        :class="`metric-${card.kind}`"
        :disabled="!overview || loading"
        @click="reveal(card.target, card.kind)"
      >
        <span>{{ card.label }} <span aria-hidden="true">↗</span></span
        ><strong>{{ card.value ?? '—' }}</strong
        ><small>{{ card.hint }}</small>
      </button>
    </div>
    <div class="overview-columns">
      <section id="alarms" class="overview-panel" tabindex="-1" :aria-busy="loading">
        <div class="overview-panel-heading">
          <div>
            <p class="eyebrow">ATTENTION</p>
            <h2>需要关注的告警</h2>
          </div>
          <RouterLink
            v-if="plantId"
            class="overview-link"
            :to="{ path: '/alarms', query: { plantId } }"
            >进入告警台 →</RouterLink
          >
        </div>
        <div class="overview-filters">
          <el-checkbox v-model="unacknowledgedOnly" :disabled="!overview" @change="refresh()"
            >仅待确认</el-checkbox
          ><span>等级优先 · 同等级未确认优先</span>
        </div>
        <el-skeleton v-if="loading" :rows="5" animated />
        <template v-else>
          <el-table
            :data="overview?.alarms ?? []"
            row-key="id"
            :empty-text="
              !overview
                ? '告警数据暂不可用'
                : unacknowledgedOnly
                  ? '暂无待确认的活动告警'
                  : '暂无活动告警，请结合数据采集状态判断'
            "
          >
            <el-table-column label="设备 / 异常" min-width="180"
              ><template #default="{ row }"
                ><RouterLink class="overview-row-title" :to="alarmLink(row.id)"
                  >{{ row.assetCode }} · {{ row.ruleName }}</RouterLink
                ><small class="overview-subline">{{ row.assetName }}</small></template
              ></el-table-column
            >
            <el-table-column label="等级" width="65"
              ><template #default="{ row }"
                ><el-tag
                  :type="['Critical', 'High'].includes(row.severity) ? 'danger' : 'warning'"
                  size="small"
                  >{{ severityLabels[row.severity] ?? row.severity }}</el-tag
                ></template
              ></el-table-column
            >
            <el-table-column label="持续 / 确认" min-width="118"
              ><template #default="{ row }"
                ><span :title="at(row.startedAt)">{{ duration(row.startedAt) }}</span
                ><small class="overview-subline">{{
                  row.acknowledgedAt ? '已确认' : '待确认'
                }}</small></template
              ></el-table-column
            >
            <el-table-column label="操作" width="90"
              ><template #default="{ row }"
                ><RouterLink class="overview-link" :to="alarmLink(row.id)"
                  >查看 / 调查</RouterLink
                ></template
              ></el-table-column
            >
          </el-table>
          <p class="overview-list-note">
            展示 {{ overview?.alarms.length ?? '—' }} /
            {{ overview?.matchingAlarms ?? '—' }} 条活动告警；确认不代表告警恢复。
          </p>
        </template>
      </section>
      <section id="orders" class="overview-panel" tabindex="-1" :aria-busy="loading">
        <div class="overview-panel-heading">
          <div>
            <p class="eyebrow">MAINTENANCE</p>
            <h2>维护待办</h2>
          </div>
          <el-checkbox v-model="mine" :disabled="!overview" @change="refresh()"
            >只看我的</el-checkbox
          >
        </div>
        <div class="overview-filters">
          <el-select
            v-model="workOrderStatus"
            aria-label="筛选工单状态"
            :disabled="!overview"
            @change="refresh()"
            ><el-option value="" label="全部未关闭" /><el-option
              v-for="(label, status) in statusLabels"
              :key="status"
              :value="status"
              :label="label" /></el-select
          ><span>按优先级与创建时间排序</span>
        </div>
        <el-skeleton v-if="loading" :rows="5" animated />
        <template v-else>
          <el-table
            :data="overview?.workOrders ?? []"
            row-key="id"
            :empty-text="
              !overview
                ? '工单数据暂不可用'
                : mine
                  ? '当前筛选下没有分配给你的工单'
                  : '当前筛选下暂无维护待办'
            "
          >
            <el-table-column label="任务 / 负责人" min-width="180"
              ><template #default="{ row }"
                ><el-button
                  link
                  type="primary"
                  class="overview-row-title"
                  @click="workOrderDrawer.open({ assetId: row.assetId, orderId: row.id })"
                  >{{ row.title }}</el-button
                ><small class="overview-subline"
                  >{{ row.assetCode }} · {{ row.number }} · {{ row.assignedTo ?? '未分配' }}</small
                ></template
              ></el-table-column
            >
            <el-table-column label="优先级" width="75"
              ><template #default="{ row }"
                ><el-tag
                  :type="['Critical', 'High'].includes(row.priority) ? 'danger' : 'info'"
                  size="small"
                  >{{ severityLabels[row.priority] ?? row.priority }}</el-tag
                ></template
              ></el-table-column
            >
            <el-table-column label="状态" width="90"
              ><template #default="{ row }">{{
                statusLabels[row.status] ?? row.status
              }}</template></el-table-column
            >
          </el-table>
          <p class="overview-list-note">
            展示 {{ overview?.workOrders.length ?? '—' }} /
            {{ overview?.matchingWorkOrders ?? '—' }} 条{{
              mine ? '分配给我的' : ''
            }}待办；不含已关闭及已取消工单。
          </p>
        </template>
      </section>
    </div>
    <section
      id="data-issues"
      class="overview-panel overview-data-panel"
      tabindex="-1"
      :aria-busy="loading"
    >
      <div class="overview-panel-heading">
        <div>
          <p class="eyebrow">DATA CONFIDENCE</p>
          <h2>数据采集异常</h2>
        </div>
        <span class="overview-note">没有告警，不等于设备健康</span>
      </div>
      <el-skeleton v-if="loading" :rows="3" animated />
      <template v-else>
        <el-table
          :data="overview?.dataIssues ?? []"
          row-key="pointId"
          :empty-text="!overview ? '测点数据暂不可用' : '当前范围内未发现启用测点的采集异常'"
        >
          <el-table-column label="设备" min-width="180"
            ><template #default="{ row }"
              ><RouterLink
                class="overview-row-title"
                :to="measurementLink(row.assetId, row.pointId)"
                >{{ row.assetCode }} · {{ row.assetName }}</RouterLink
              ></template
            ></el-table-column
          >
          <el-table-column prop="pointName" label="测点" min-width="130" />
          <el-table-column label="异常原因" min-width="130"
            ><template #default="{ row }"
              ><el-tag type="warning" size="small">{{
                issueLabels[row.reason] ?? row.reason
              }}</el-tag></template
            ></el-table-column
          >
          <el-table-column label="最后接收" min-width="180"
            ><template #default="{ row }">{{ at(row.lastReceivedAt) }}</template></el-table-column
          >
          <el-table-column label="操作" width="100"
            ><template #default="{ row }"
              ><RouterLink class="overview-link" :to="measurementLink(row.assetId, row.pointId)"
                >查看测量 →</RouterLink
              ></template
            ></el-table-column
          >
        </el-table>
        <p class="overview-list-note">
          展示 {{ overview?.dataIssues.length ?? '—' }} /
          {{ overview?.counts.dataIssues ?? '—' }} 个异常测点；停用测点与归档设备不计入。
        </p>
      </template>
    </section>
    <div v-if="overview" class="overview-bottom-actions">
      <el-button v-if="limit === 5" @click="expandLists(true)">展开更多（每区最多 50 条）</el-button
      ><el-button v-else @click="expandLists(false)">收起列表</el-button
      ><span>摘要为当前工厂完整统计，不受列表条数及待办筛选影响。</span>
    </div>
  </template>
  <footer class="overview-connection" :class="{ 'connection-error': service.includes('不可用') }">
    <span>系统状态 · {{ service }}</span
    ><span v-if="session.user">{{ realtimeStatus }} · 每 15 秒补查</span
    ><el-button text size="small" @click="checkService">检查连接</el-button>
  </footer>
</template>

<style scoped>
.overview-heading {
  padding-top: 32px;
}
.overview-toolbar,
.overview-filters,
.overview-panel-heading,
.overview-bottom-actions,
.overview-connection {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
}
.overview-toolbar {
  margin-bottom: 24px;
  color: var(--muted);
  font-size: 12px;
}
.overview-toolbar .el-form-item {
  margin: 0;
}
.overview-toolbar .el-select {
  width: 230px;
}
.overview-metrics {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 16px;
  margin-bottom: 24px;
}
button.overview-metric {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  text-align: left;
  padding: 20px;
  border: 1px solid var(--line);
  border-top: 3px solid var(--green);
  border-radius: 4px;
  background: #fff;
  color: #183732;
  white-space: normal;
}
button.overview-metric:hover:enabled {
  background: #edf3ed;
  border-color: #8aa89b;
}
button.overview-metric:disabled {
  cursor: default;
  opacity: 0.75;
}
.overview-metric > span {
  display: flex;
  justify-content: space-between;
  font-size: 13px;
}
.overview-metric strong {
  margin: 12px 0 8px;
  font-size: 36px;
  line-height: 1;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}
.overview-metric small {
  color: var(--muted);
  font-size: 11px;
  line-height: 1.6;
}
button.metric-unacknowledged {
  border-top-color: #a34730;
}
button.metric-data {
  border-top-color: #a27b30;
}
.overview-columns {
  display: grid;
  grid-template-columns: minmax(0, 1.2fr) minmax(0, 1fr);
  gap: 24px;
  margin-bottom: 24px;
}
.overview-panel {
  min-width: 0;
  padding: 24px;
  border: 1px solid var(--line);
  border-radius: 4px;
  background: #fff;
  scroll-margin-top: 24px;
}
.overview-panel-heading {
  margin-bottom: 16px;
}
.overview-panel-heading h2,
.overview-welcome h2 {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
}
.overview-panel-heading .eyebrow {
  margin-bottom: 8px;
}
.overview-filters {
  min-height: 40px;
  margin-bottom: 16px;
  font-size: 11px;
  color: var(--muted);
}
.overview-filters .el-select {
  width: 150px;
}
.overview-link {
  color: var(--green);
  font-size: 12px;
  font-weight: 600;
}
.overview-row-title {
  color: var(--green);
  font-weight: 600;
  overflow-wrap: anywhere;
}
.overview-row-title:hover,
.overview-link:hover {
  text-decoration: underline;
}
.overview-subline {
  display: block;
  color: var(--muted);
  font-size: 11px;
  line-height: 1.7;
  margin-top: 4px;
  overflow-wrap: anywhere;
}
.overview-list-note,
.overview-note {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.8;
}
.overview-list-note {
  margin: 16px 0 0;
}
.overview-bottom-actions {
  margin-top: 16px;
  color: var(--muted);
  font-size: 12px;
}
.overview-connection {
  justify-content: flex-start;
  border-top: 1px solid var(--line);
  margin-top: 32px;
  padding-top: 16px;
  color: var(--muted);
  font-size: 11px;
}
.connection-error {
  color: #a34730;
}
.overview-empty,
.overview-welcome {
  padding: 32px;
  border: 1px solid var(--line);
  border-radius: 4px;
  background: white;
}
.overview-welcome p {
  color: var(--muted);
  font-size: 13px;
}
.overview-panel:focus-visible,
.overview-metric:focus-visible,
a:focus-visible {
  outline: 2px solid var(--green);
  outline-offset: 4px;
}
@media (max-width: 1200px) {
  .overview-columns {
    grid-template-columns: minmax(0, 1fr);
  }
}
@media (max-width: 720px) {
  .overview-metrics {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 12px;
  }
  .overview-panel {
    padding: 16px;
  }
  button.overview-metric {
    padding: 16px;
  }
  .overview-toolbar .el-select {
    width: 190px;
  }
}
</style>
