<script setup lang="ts">
import { computed, nextTick, onUnmounted, ref, watch } from 'vue'
import { request } from '../api'
import type { components } from '../generated/api'
import TelemetryConfiguration from './TelemetryConfiguration.vue'
import { subscribePlantEvents } from '../realtime'
type Point = components['schemas']['PointOverview']
type Trend = components['schemas']['MeasurementTrend']
const props = defineProps<{ assetId: string; plantId: string; admin: boolean; archived: boolean }>()
const points = ref<Point[]>([]), pointId = ref(''), trend = ref<Trend | null>(null)
const error = ref(''), checked = ref(''), busy = ref(false), auto = ref(true), configure = ref(false)
const windowMinutes = ref(15), bucket = ref('raw')
const observationLatencies = ref<number[]>([])
let recordedReading: string | null = null
const point = computed(() => points.value.find(p => p.id === pointId.value))
let generation = 0, timer: ReturnType<typeof setInterval> | undefined
function at(value?: string | null) { return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—' }
function value(v?: number | string | null) { return v == null ? '—' : Number(v).toFixed(2) }
async function refresh() {
  if (busy.value) return
  const current = ++generation, asset = props.assetId
  busy.value = true
  try {
    const list = await request<Point[]>(`/assets/${asset}/measurement-points`)
    if (current !== generation) return
    points.value = list
    if (!list.some(x => x.id === pointId.value)) pointId.value = list.find(x => x.code === 'VIBRATION_RMS')?.id ?? list[0]?.id ?? ''
    const end = bucket.value === '1m' ? new Date(Math.floor(Date.now() / 60000) * 60000) : new Date()
    const start = new Date(end.getTime() - windowMinutes.value * 60000)
    const result = pointId.value ? await request<Trend>(`/assets/${asset}/measurements?pointId=${pointId.value}&from=${encodeURIComponent(start.toISOString())}&to=${encodeURIComponent(end.toISOString())}&bucket=${bucket.value}`) : null
    if (current !== generation) return
    trend.value = result; checked.value = end.toISOString(); error.value = ''
    await nextTick()
    const observed = point.value?.latestObserved
    if (observed && recordedReading !== observed.id) {
      recordedReading = observed.id
      const delay = Date.now() - Date.parse(observed.receivedAt)
      if (delay >= 0 && delay < 10000) { observationLatencies.value.push(delay); if (observationLatencies.value.length > 2000) observationLatencies.value.shift() }
    }
  } catch (e) {
    if (current === generation) { error.value = e instanceof Error ? e.message : '测量读取失败'; points.value = []; trend.value = null; checked.value = '' }
  } finally { if (current === generation) busy.value = false }
}
function change() { generation++; busy.value = false; trend.value = null; void refresh() }
watch(() => props.assetId, () => { generation++; busy.value = false; points.value = []; pointId.value = ''; trend.value = null; error.value = ''; configure.value = false; void refresh() }, { immediate: true })
timer = setInterval(() => { if (auto.value && !configure.value) void refresh() }, 5000)
const unsubscribe = subscribePlantEvents(event => { if (!configure.value && auto.value && (!event || event.plantId === props.plantId && event.eventType === 'MeasurementChanged')) void refresh() })
onUnmounted(() => { generation++; clearInterval(timer); unsubscribe() })
const chart = computed(() => {
  const t = trend.value
  if (!t) return { segments: [] as string[], dots: [] as { x: number; y: number }[], max: 1, min: 0 }
  const items = t.bucket === 'raw'
    ? t.samples.map(s => ({ at: s.effectiveAt, value: s.value == null ? null : Number(s.value), valid: s.quality === 'Good' && !s.timeAnomaly, gap: s.gapBefore }))
    : t.buckets.map(s => ({ at: s.start, value: s.average == null ? null : Number(s.average), valid: !s.hasGap, gap: s.hasGap }))
  const values = items.filter(s => s.valid && s.value != null && Number.isFinite(s.value)).map(s => s.value!)
  const max = Math.max(1, ...values) * 1.15, min = Math.min(0, ...values)
  const from = Date.parse(t.from), to = Date.parse(t.to)
  const segments: string[] = [], dots: { x: number; y: number }[] = []; let line: string[] = []
  function flush() { if (line.length > 1) segments.push(line.join(' ')); line = [] }
  for (const s of items) {
    if (!s.valid || s.value == null || !Number.isFinite(s.value)) { flush(); continue }
    if (s.gap) flush()
    const x = 50 + (Date.parse(s.at) - from) / (to - from) * 880, y = 195 - (s.value - min) / (max - min) * 170
    line.push(`${x},${y}`); dots.push({ x, y })
  }
  flush(); return { segments, dots, max, min }
})
const quality = computed(() => {
  const t = trend.value
  if (!t) return ''
  const bad = t.samples.filter(x => x.quality === 'Bad').length + t.buckets.reduce((a, b) => a + Number(b.badCount), 0)
  const uncertain = t.samples.filter(x => x.quality === 'Uncertain').length + t.buckets.reduce((a, b) => a + Number(b.uncertainCount), 0)
  const anomalies = t.samples.filter(x => x.timeAnomaly).length + t.buckets.reduce((a, b) => a + Number(b.anomalyCount), 0)
  return `Bad ${bad} · Uncertain ${uncertain} · 时间异常 ${anomalies}`
})
</script>
<template>
  <section class="measurement-panel" :aria-busy="busy" :data-observation-latencies="JSON.stringify(observationLatencies)">
    <div class="section-title"><div><p class="eyebrow">MEASUREMENTS / SIMULATION</p><h2>观测、质量与时间。</h2></div><div class="actions"><button :disabled="busy" class="secondary" @click="refresh">刷新测量</button><button v-if="admin && !archived" class="secondary" @click="configure = !configure">{{ configure ? '关闭配置' : '配置测量' }}</button></div></div>
    <p v-if="error" role="alert" class="error-banner">{{ error }} 已隐藏未验证的测量状态，请重试。</p>
    <div class="measurement-toolbar"><label>点位<select v-model="pointId" :disabled="busy" @change="change"><option v-if="!points.length" value="">暂无点位</option><option v-for="p in points" :key="p.id" :value="p.id">{{ p.code }} · {{ p.name }}{{ p.enabled ? '' : '（已停用）' }}</option></select></label><label>时间窗口<select v-model="windowMinutes" @change="change"><option :value="15">最近 15 分钟</option><option :value="60">最近 1 小时</option><option :value="1440">最近 24 小时</option></select></label><label>粒度<select v-model="bucket" @change="change"><option value="raw">原始观测</option><option value="1m">分钟统计</option></select></label><label class="check-label"><input v-model="auto" type="checkbox">每 5 秒刷新</label></div>
    <template v-if="point">
      <div class="measurement-summary"><div><p class="eyebrow">LATEST OBSERVED</p><strong>{{ value(point.latestObserved?.value) }} <small>{{ point.canonicalUnit }}</small></strong><p><span class="quality-tag" :class="point.latestObserved?.isTrusted ? 'trusted' : 'untrusted'">{{ point.latestObserved?.quality ?? '暂无观测' }} · {{ point.latestObserved ? point.latestObserved.isFresh ? '新鲜' : '陈旧' : '未接收' }}</span></p><p class="path-note">{{ point.latestObserved?.isTrusted ? '当前可信观测' : '不可作为当前可信值' }}<br>有效时间 {{ at(point.latestObserved?.effectiveAt) }}</p></div><div><p class="eyebrow">LAST GOOD / 历史可信值</p><strong>{{ value(point.lastGood?.value) }} <small>{{ point.canonicalUnit }}</small></strong><p class="path-note">{{ point.lastGood ? point.lastGood.isFresh ? 'Good · 仍在新鲜度窗口内' : 'Good · 已陈旧，仅供历史参考' : '尚无可信读数' }}<br>有效时间 {{ at(point.lastGood?.effectiveAt) }}</p></div><div><p class="eyebrow">当前采集配置</p><p>{{ point.sourceKind ?? '未启用映射' }} · {{ point.enabled ? '点位启用' : '点位停用' }}</p><p class="path-note">{{ point.sourceKey ?? '—' }}<br>映射 v{{ point.mappingVersion ?? '—' }} · 点位 v{{ point.version }}<br>最后接收 {{ at(point.lastReceivedAt) }}<br>陈旧阈值 {{ Number(point.staleAfterMs) / 1000 }} 秒</p></div></div>
      <div v-if="trend" class="trend-chart"><div class="section-title"><h3>{{ point.name }} <small>/ {{ trend.unit }}</small></h3><span class="path-note">{{ quality }}</span></div><svg viewBox="0 0 960 230" role="img" :aria-label="`${point.name}趋势。仅连接有效 Good 数据，坏值与缺口断线。`"><line x1="50" y1="195" x2="930" y2="195" stroke="#c7d4ce"/><line x1="50" y1="25" x2="930" y2="25" stroke="#e4ebe7"/><text x="8" y="30">{{ chart.max.toFixed(1) }}</text><text x="8" y="198">{{ chart.min.toFixed(1) }}</text><polyline v-for="(line, i) in chart.segments" :key="i" :points="line" fill="none" stroke="#196d58" stroke-width="2.5"/><circle v-for="(dot, i) in chart.dots" :key="i" :cx="dot.x" :cy="dot.y" r="1.8" fill="#196d58"/><text x="50" y="222">{{ at(trend.from) }}</text><text x="930" y="222" text-anchor="end">{{ at(trend.to) }}</text></svg><p v-if="!chart.dots.length" class="path-note">此窗口没有可连续展示的 Good 数据。分钟统计含缺口时仍可在下表查看。</p><p class="path-note">Bad、Uncertain、时间异常与采样缺口均断线；没有观测的区间保持空白。{{ trend.leadingGap || trend.trailingGap ? '窗口边缘存在缺口。' : '' }} 分钟统计使用完整 UTC 分钟。页面时间采用本机时区；接口和存储为 UTC。</p></div>
      <details v-if="trend"><summary>查看质量、时间与来源明细</summary><div class="telemetry-table"><table v-if="trend.bucket === 'raw'"><thead><tr><th>有效时间</th><th>值 / 质量</th><th>时间依据</th><th>来源 / 映射版本</th><th>状态 / 接收时间</th></tr></thead><tbody><tr v-for="s in trend.samples.slice(-100).reverse()" :key="s.id"><td>{{ at(s.effectiveAt) }}{{ s.gapBefore ? ' · 缺口' : '' }}{{ s.timeAnomaly ? ' · 时间异常' : '' }}</td><td>{{ value(s.value) }} {{ s.quality }}</td><td>{{ s.timestampOrigin }}<br>源 {{ at(s.sourceTimestamp) }}<br>服务器 {{ at(s.serverTimestamp) }}</td><td>{{ s.sourceKind }} / v{{ s.mappingVersion }}<br>{{ s.sourceMappingId }}</td><td>{{ s.statusSymbol ?? '—' }} / {{ s.rawStatusCode ?? '—' }}<br>{{ at(s.receivedAt) }}</td></tr></tbody></table><table v-else><thead><tr><th>分钟</th><th>最小 / 最大 / 平均</th><th>Good / Uncertain / Bad</th><th>覆盖率 / 缺口</th></tr></thead><tbody><tr v-for="b in trend.buckets.slice(-100).reverse()" :key="b.start"><td>{{ at(b.start) }}</td><td>{{ value(b.min) }} / {{ value(b.max) }} / {{ value(b.average) }}</td><td>{{ b.goodCount }} / {{ b.uncertainCount }} / {{ b.badCount }}</td><td>{{ (Number(b.coverageRatio) * 100).toFixed(0) }}% · {{ b.hasGap ? '有缺口' : '完整' }}</td></tr></tbody></table></div><p class="path-note">展示最近 100 条；窗口查询保留全部返回数据。</p></details>
    </template>
    <p v-else-if="!error" class="path-note">{{ busy ? '正在读取测量…' : '此设备尚无测量点位。' }}</p>
    <p v-if="checked" class="path-note">最近成功读取 {{ at(checked) }} · {{ auto && !configure ? '自动刷新中' : '自动刷新已暂停，当前展示为读取时的快照' }}</p>
    <TelemetryConfiguration v-if="configure" :asset-id="assetId" :plant-id="plantId" :points="points" @saved="change" />
  </section>
</template>


