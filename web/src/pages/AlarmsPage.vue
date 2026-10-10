<script setup lang="ts">
import InvestigationPanel from '../components/InvestigationPanel.vue'
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { listAll, request, type DirectoryRecord } from '../api'
import { useSession } from '../stores/session'
import type { components } from '../generated/api'
import AlarmRuleConfiguration from '../components/AlarmRuleConfiguration.vue'
import { subscribePlantEvents } from '../realtime'
type Alarm = components['schemas']['AlarmDto']
type Page = components['schemas']['AlarmPage']
type Event = components['schemas']['AlarmEventDto']
const session = useSession(),
  route = useRoute()
const plants = ref<DirectoryRecord[]>([]),
  plantId = ref(''),
  assetId = ref(''),
  filter = ref('active')
const rows = ref<Alarm[]>([]),
  nextCursor = ref<string | null>(null),
  selectedId = ref('')
const alarm = ref<Alarm | null>(null),
  events = ref<Event[]>([]),
  error = ref(''),
  actionError = ref(''),
  notice = ref(''),
  checked = ref('')
const busy = ref(false),
  auto = ref(true),
  configuring = ref(false)
let generation = 0
const canAcknowledge = computed(
  () =>
    session.user?.roles.some((x) => ['Operator', 'MaintenanceEngineer', 'Admin'].includes(x)) ??
    false,
)
const canMaintain = computed(
  () => session.user?.roles.some((x) => ['MaintenanceEngineer', 'Admin'].includes(x)) ?? false,
)
const counts = computed(() => ({
  active: rows.value.filter((x) => x.state === 'Active').length,
  acknowledged: rows.value.filter((x) => x.acknowledgedAt).length,
  unknown: rows.value.filter((x) => !x.currentObservationTrusted).length,
}))
const snapshot = computed(() => {
  try {
    return JSON.parse(alarm.value?.thresholdSnapshot ?? '{}') as Record<string, unknown>
  } catch {
    return {}
  }
})
function at(value?: string | null) {
  return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
}
function state(value: string) {
  return value === 'Active' ? '活动' : '已恢复'
}
const eventNames: Record<string, string> = {
  Raised: '触发',
  Acknowledged: '人工确认',
  Recovered: '可信低值恢复',
}
async function refresh(more = false) {
  if (busy.value || !plantId.value) return
  busy.value = true
  const current = ++generation
  try {
    await session.load()
    const page = await request<Page>(
      `/alarms?plantId=${plantId.value}&limit=50${assetId.value ? '&assetId=' + assetId.value : ''}${filter.value === 'all' ? '' : '&active=' + (filter.value === 'active')}${more && nextCursor.value ? '&cursor=' + encodeURIComponent(nextCursor.value) : ''}`,
    )
    if (current !== generation) return
    const detail = selectedId.value ? await request<Alarm>(`/alarms/${selectedId.value}`) : null
    const history = detail ? await request<Event[]>(`/alarms/${detail.id}/events`) : []
    if (current !== generation) return
    rows.value = more
      ? [...rows.value, ...page.items.filter((x) => !rows.value.some((r) => r.id === x.id))]
      : page.items
    nextCursor.value = page.nextCursor
    alarm.value = detail
    events.value = history
    checked.value = new Date().toISOString()
    error.value = ''
  } catch (e) {
    if (current === generation) {
      error.value = e instanceof Error ? e.message : '告警读取失败'
      rows.value = []
      alarm.value = null
      events.value = []
      nextCursor.value = null
      checked.value = ''
    }
  } finally {
    if (current === generation) busy.value = false
  }
}
function change(clearAsset = false) {
  generation++
  busy.value = false
  rows.value = []
  selectedId.value = ''
  alarm.value = null
  events.value = []
  nextCursor.value = null
  configuring.value = false
  actionError.value = ''
  notice.value = ''
  if (clearAsset) assetId.value = ''
  void refresh()
}
function clearAssetFilter() {
  assetId.value = ''
  change()
}
async function select(id: string) {
  selectedId.value = id
  actionError.value = ''
  notice.value = ''
  await refresh()
}
async function acknowledge() {
  if (!alarm.value) return
  const id = alarm.value.id,
    version = alarm.value.version
  busy.value = true
  actionError.value = ''
  notice.value = ''
  try {
    const result = await request<Alarm>(`/alarms/${id}/acknowledge`, 'POST', { version })
    alarm.value = result
    notice.value = '已记录确认人和时间。确认不改变告警的活动或恢复状态。'
  } catch (e) {
    actionError.value = e instanceof Error ? e.message : '确认失败'
  } finally {
    busy.value = false
    await refresh()
  }
}
onMounted(async () => {
  busy.value = true
  try {
    plants.value = await listAll('/plants')
    plantId.value =
      plants.value.find((x) => x.id === route.query.plantId)?.id ??
      plants.value.find((x) => !x.isArchived)?.id ??
      plants.value[0]?.id ??
      ''
    assetId.value = typeof route.query.assetId === 'string' ? route.query.assetId : ''
  } catch (e) {
    error.value = e instanceof Error ? e.message : '工厂读取失败'
  } finally {
    busy.value = false
    await refresh()
  }
})
const timer = setInterval(() => {
  if (auto.value && !configuring.value) void refresh()
}, 5000)
const unsubscribe = subscribePlantEvents((event) => {
  if (
    !configuring.value &&
    auto.value &&
    (!event || (event.plantId === plantId.value && event.eventType === 'AlarmChanged'))
  )
    void refresh()
})
onUnmounted(() => {
  generation++
  clearInterval(timer)
  unsubscribe()
})
</script>
<template>
  <header class="topbar">
    <span>工作空间 / 告警台</span
    ><span class="stage-chip">{{ session.user?.roles.join(' / ') }}</span>
  </header>
  <section class="page-heading">
    <div>
      <p class="eyebrow">ALARM DESK / 03</p>
      <h1>看见异常，确认收到。</h1>
      <p class="intro">告警由可信测量与确定性规则产生。人工确认与数据恢复各自独立。</p>
    </div>
    <button :disabled="busy" @click="refresh()">刷新告警</button>
  </section>
  <p v-if="error" role="alert" class="error-banner">
    {{ error }} 已隐藏未验证的告警状态。<RouterLink v-if="!session.user" to="/login"
      >重新登录</RouterLink
    >
  </p>
  <p v-if="actionError" role="alert" class="error-banner">{{ actionError }}</p>
  <p v-if="notice" role="status" class="success-banner">{{ notice }}</p>
  <div class="measurement-toolbar alarm-toolbar">
    <label
      >工厂<select v-model="plantId" :disabled="busy" @change="change(true)">
        <option v-if="!plants.length" value="">无授权工厂</option>
        <option v-for="p in plants" :key="p.id" :value="p.id">
          {{ p.name }}{{ p.isArchived ? '（已归档）' : '' }}
        </option>
      </select></label
    ><label
      >告警状态<select v-model="filter" :disabled="busy" @change="change()">
        <option value="active">活动告警</option>
        <option value="recovered">已恢复告警</option>
        <option value="all">全部历史</option>
      </select></label
    ><label class="check-label"><input v-model="auto" type="checkbox" />每 5 秒刷新</label
    ><button v-if="assetId" class="secondary" @click="clearAssetFilter">清除设备筛选</button
    ><button
      v-if="session.admin && plantId"
      class="secondary"
      :disabled="busy"
      @click="configuring = !configuring"
    >
      {{ configuring ? '关闭规则配置' : '配置告警规则' }}
    </button>
  </div>
  <div v-if="!error" class="alarm-counts">
    <div>
      <span>已加载 · 活动</span><strong>{{ counts.active }}</strong>
    </div>
    <div>
      <span>已加载 · 已确认</span><strong>{{ counts.acknowledged }}</strong>
    </div>
    <div>
      <span>已加载 · 当前状态无法验证</span><strong>{{ counts.unknown }}</strong>
    </div>
  </div>
  <section class="alarm-list" :aria-busy="busy">
    <div class="section-title">
      <h2>
        {{ filter === 'active' ? '活动告警' : filter === 'recovered' ? '已恢复告警' : '告警历史' }}
      </h2>
      <span class="path-note">{{ rows.length }} 条已加载 · 每页 50 条</span>
    </div>
    <div class="alarm-table">
      <div class="alarm-row-heading">
        <span>设备 / 规则</span><span>业务状态</span><span>人工确认</span><span>发生时间</span>
      </div>
      <button
        v-for="item in rows"
        :key="item.id"
        class="alarm-row"
        :class="{ selected: selectedId === item.id, active: item.state === 'Active' }"
        :disabled="busy"
        @click="select(item.id)"
      >
        <span
          ><b>{{ item.assetCode }} · {{ item.ruleName }}</b
          ><small
            >{{ item.pointCode }} · {{ item.severity }} · 触发
            {{ Number(item.triggerValue).toFixed(2) }}</small
          ></span
        ><span class="alarm-state"
          >{{ state(item.state)
          }}<small>{{
            item.currentObservationTrusted ? '当前观测可信' : '当前状态无法验证'
          }}</small></span
        ><span
          >{{ item.acknowledgedAt ? '已确认' : '未确认'
          }}<small>{{
            item.acknowledgedAt ? at(item.acknowledgedAt) : '确认不会使告警恢复'
          }}</small></span
        ><span
          >{{ at(item.startedAt) }}<small>v{{ item.version }}</small></span
        >
      </button>
      <p v-if="!rows.length && !error" class="path-note">
        {{ busy ? '正在读取告警…' : '此筛选下暂无告警。' }}
      </p>
    </div>
    <button v-if="nextCursor" class="secondary" :disabled="busy" @click="refresh(true)">
      加载更多告警
    </button>
  </section>
  <section v-if="alarm" class="alarm-detail">
    <div class="section-title">
      <div>
        <p class="eyebrow">
          {{ alarm.assetCode }} / {{ alarm.pointCode }} / RULE V{{ alarm.ruleVersion }}
        </p>
        <h2>{{ snapshot.Name ?? alarm.ruleName }}</h2>
      </div>
      <button v-if="canAcknowledge && !alarm.acknowledgedAt" :disabled="busy" @click="acknowledge">
        确认告警</button
      ><span v-else class="quality-tag">{{
        alarm.acknowledgedAt ? '已记录人工确认' : '当前角色只读'
      }}</span>
    </div>
    <p class="alarm-status-note">
      <strong>{{ state(alarm.state) }} · {{ alarm.acknowledgedAt ? '已确认' : '未确认' }}</strong
      ><span>{{
        alarm.currentObservationTrusted
          ? '当前观测可信'
          : '当前状态无法验证：测量质量、陈旧或采集配置不可用。此提示不会自动恢复活动告警。'
      }}</span>
    </p>
    <dl class="detail-grid">
      <div>
        <dt>设备</dt>
        <dd>{{ alarm.assetName }}</dd>
      </div>
      <div>
        <dt>触发值 / 标准单位</dt>
        <dd>{{ Number(alarm.triggerValue).toFixed(2) }} {{ snapshot.Unit }}</dd>
      </div>
      <div>
        <dt>触发条件快照</dt>
        <dd>&gt; {{ snapshot.RaiseAbove }}，持续 {{ Number(snapshot.RaiseForMs) / 1000 }} 秒</dd>
      </div>
      <div>
        <dt>恢复条件快照</dt>
        <dd>&lt; {{ snapshot.ClearBelow }}，持续 {{ Number(snapshot.ClearForMs) / 1000 }} 秒</dd>
      </div>
      <div>
        <dt>最大样本间隔</dt>
        <dd>{{ Number(snapshot.MaxSampleGapMs) / 1000 }} 秒</dd>
      </div>
      <div>
        <dt>恢复时间</dt>
        <dd>{{ at(alarm.recoveredAt) }}</dd>
      </div>
      <div>
        <dt>确认人 / 时间</dt>
        <dd>{{ alarm.acknowledgedByUserId ?? '—' }}<br />{{ at(alarm.acknowledgedAt) }}</dd>
      </div>
      <div>
        <dt>最后评估 / 版本</dt>
        <dd>{{ at(alarm.lastEvaluatedAt) }} / v{{ alarm.version }}</dd>
      </div>
    </dl>
    <p class="path-note">
      <RouterLink :to="{ path: '/assets', query: { assetId: alarm.assetId, alarmId: alarm.id } }">{{
        canMaintain ? '查看设备并创建关联工单 →' : '查看设备与维护历史 →'
      }}</RouterLink>
    </p>
    <InvestigationPanel :key="alarm.id" :alarm-id="alarm.id" />
    <h3>事件记录</h3>
    <ol class="alarm-timeline">
      <li v-for="event in events" :key="event.id">
        <b>{{ eventNames[event.eventType] ?? event.eventType }}</b
        ><span>{{ at(event.occurredAt) }} · {{ event.actorUserId ?? '规则引擎' }}</span
        ><small v-if="event.readingId">观测 {{ event.readingId }}</small>
      </li>
    </ol>
    <p v-if="!alarm.triggerReadingAvailable" class="path-note">
      原始样本已过保留期；当前展示告警触发值、阈值快照与保留事件，观测标识仅用于追溯。
    </p>
    <details>
      <summary>追溯标识</summary>
      <p class="path-note">
        告警 {{ alarm.id }}<br />规则 {{ alarm.ruleId }} / v{{ alarm.ruleVersion }}<br />触发观测
        {{ alarm.triggerReadingId }}
      </p>
    </details>
  </section>
  <AlarmRuleConfiguration
    v-if="configuring"
    :key="plantId"
    :plant-id="plantId"
    :initial-asset-id="assetId"
    @saved="refresh()"
  />
  <p v-if="checked" class="path-note">
    最近成功读取 {{ at(checked) }} ·
    {{ auto && !configuring ? '自动刷新中' : '自动刷新已暂停，当前为读取时的快照' }} ·
    页面时间为本机时区。
  </p>
</template>
