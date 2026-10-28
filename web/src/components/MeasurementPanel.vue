<script setup lang="ts">
import { messageRef, t } from '../i18n'
import { formatDateTime as at } from '../time'
import { computed, nextTick, onUnmounted, ref, watch } from 'vue'
import { request } from '../api'
import type { components } from '../generated/api'
import SidePanel from './SidePanel.vue'
import TelemetryConfiguration from './TelemetryConfiguration.vue'
const configuration = ref<InstanceType<typeof TelemetryConfiguration>>()
import { subscribePlantEvents } from '../realtime'
type Point = components['schemas']['PointOverview']
type Trend = components['schemas']['MeasurementTrend']
type Source = components['schemas']['SourceDto']
const props = defineProps<{
  assetId: string
  plantId: string
  admin: boolean
  archived: boolean
  initialPointId?: string
}>()
const points = ref<Point[]>([]),
  pointId = ref(''),
  trend = ref<Trend | null>(null)
const sources = ref<Source[]>([])
const source = computed(() => sources.value.find((s) => s.id === point.value?.dataSourceId))
function connectionState() {
  if (!source.value?.enabled) return t('来源停用')
  const health = source.value.health
  if (!health || !health.isCurrent) return t('状态未确认（Worker 心跳过期或配置已变化）')
  const labels: Record<string, string> = {
    Connecting: t('连接中'),
    Connected: t('已连接'),
    Reconnecting: t('断线重连中'),
    Faulted: t('配置或安全校验失败，等待调整配置'),
    Stopped: t('采集已停止'),
  }
  return labels[health.state] ?? t('状态未知')
}
const error = messageRef(),
  checked = ref(''),
  busy = ref(false),
  auto = ref(true),
  configure = ref(false)
const windowMinutes = ref(15),
  bucket = ref('raw')
const observationLatencies = ref<number[]>([])
let recordedReading: string | null = null
const point = computed(() => points.value.find((p) => p.id === pointId.value))
let generation = 0
let refreshing = false
function value(v?: number | string | null) {
  return v == null ? '—' : Number(v).toFixed(2)
}
async function refresh(background = false) {
  if (refreshing) return
  refreshing = true
  const current = ++generation,
    asset = props.assetId
  if (!background) busy.value = true
  try {
    const [list, sourceList] = await Promise.all([
      request<Point[]>(`/assets/${asset}/measurement-points`),
      request<Source[]>(`/plants/${props.plantId}/data-sources`),
    ])
    if (current !== generation) return
    points.value = list
    sources.value = sourceList
    if (!list.some((x) => x.id === pointId.value))
      pointId.value = list.find((x) => x.code === 'VIBRATION_RMS')?.id ?? list[0]?.id ?? ''
    const end =
      bucket.value === '1m' ? new Date(Math.floor(Date.now() / 60000) * 60000) : new Date()
    const start = new Date(end.getTime() - windowMinutes.value * 60000)
    const result = pointId.value
      ? await request<Trend>(
          `/assets/${asset}/measurements?pointId=${pointId.value}&from=${encodeURIComponent(start.toISOString())}&to=${encodeURIComponent(end.toISOString())}&bucket=${bucket.value}`,
        )
      : null
    if (current !== generation) return
    trend.value = result
    checked.value = end.toISOString()
    error.value = ''
    await nextTick()
    const observed = point.value?.latestObserved
    if (observed && recordedReading !== observed.id) {
      recordedReading = observed.id
      const delay = Date.now() - Date.parse(observed.receivedAt)
      if (delay >= 0 && delay < 10000) {
        observationLatencies.value.push(delay)
        if (observationLatencies.value.length > 2000) observationLatencies.value.shift()
      }
    }
  } catch (e) {
    if (current === generation) {
      error.value = () => (e instanceof Error ? e.message : t('测量读取失败'))
      points.value = []
      sources.value = []
      trend.value = null
      checked.value = ''
    }
  } finally {
    if (current === generation) {
      busy.value = false
      refreshing = false
    }
  }
}
function change() {
  generation++
  refreshing = false
  busy.value = false
  trend.value = null
  void refresh()
}
watch(
  () => props.assetId,
  () => {
    generation++
    refreshing = false
    busy.value = false
    points.value = []
    pointId.value = props.initialPointId ?? ''
    trend.value = null
    error.value = ''
    configure.value = false
    void refresh()
  },
  { immediate: true },
)
const timer = setInterval(() => {
  if (auto.value && !configure.value) void refresh(true)
}, 5000)
const unsubscribe = subscribePlantEvents((event) => {
  if (
    !configure.value &&
    auto.value &&
    (!event || (event.plantId === props.plantId && event.eventType === 'MeasurementChanged'))
  )
    void refresh(true)
})
onUnmounted(() => {
  generation++
  clearInterval(timer)
  unsubscribe()
})
const chart = computed(() => {
  const data = trend.value
  if (!data)
    return { segments: [] as string[], dots: [] as { x: number; y: number }[], max: 1, min: 0 }
  const items =
    data.bucket === 'raw'
      ? data.samples.map((s) => ({
          at: s.effectiveAt,
          value: s.value == null ? null : Number(s.value),
          valid:
            s.quality === 'Good' && !s.timeAnomaly && (Number(s.rawStatusCode) & 0xc80) !== 0x480,
          gap: s.gapBefore,
        }))
      : data.buckets.map((s) => ({
          at: s.start,
          value: s.average == null ? null : Number(s.average),
          valid: !s.hasGap,
          gap: s.hasGap,
        }))
  const values = items
    .filter((s) => s.valid && s.value != null && Number.isFinite(s.value))
    .map((s) => s.value!)
  const max = Math.max(1, ...values) * 1.15,
    min = Math.min(0, ...values)
  const from = Date.parse(data.from),
    to = Date.parse(data.to)
  const segments: string[] = [],
    dots: { x: number; y: number }[] = []
  let line: string[] = []
  function flush() {
    if (line.length > 1) segments.push(line.join(' '))
    line = []
  }
  for (const s of items) {
    if (!s.valid || s.value == null || !Number.isFinite(s.value)) {
      flush()
      continue
    }
    if (s.gap) flush()
    const x = 50 + ((Date.parse(s.at) - from) / (to - from)) * 880,
      y = 195 - ((s.value - min) / (max - min)) * 170
    line.push(`${x},${y}`)
    dots.push({ x, y })
  }
  flush()
  return { segments, dots, max, min }
})
const quality = computed(() => {
  const data = trend.value
  if (!data) return ''
  const bad =
    data.samples.filter((x) => x.quality === 'Bad').length +
    data.buckets.reduce((a, b) => a + Number(b.badCount), 0)
  const uncertain =
    data.samples.filter((x) => x.quality === 'Uncertain').length +
    data.buckets.reduce((a, b) => a + Number(b.uncertainCount), 0)
  const anomalies =
    data.samples.filter((x) => x.timeAnomaly).length +
    data.buckets.reduce((a, b) => a + Number(b.anomalyCount), 0)
  return t('Bad {0} · Uncertain {1} · 时间异常 {2}', [bad, uncertain, anomalies])
})
</script>
<template>
  <section
    class="measurement-panel"
    :aria-busy="busy"
    :data-observation-latencies="JSON.stringify(observationLatencies)"
  >
    <div class="section-title">
      <div>
        <p class="eyebrow">MEASUREMENTS / QUALITY</p>
        <h2>{{ t('观测、质量与时间。') }}</h2>
      </div>
      <div class="actions">
        <el-button :disabled="busy" @click="refresh()">{{ t('刷新测量') }}</el-button
        ><el-button v-if="admin && !archived" @click="configure = !configure">
          {{ configure ? t('关闭配置') : t('配置测量') }}
        </el-button>
      </div>
    </div>
    <p v-if="error" role="alert" class="error-banner">
      {{ error }} {{ t('已隐藏未验证的测量状态，请重试。') }}
    </p>
    <div class="measurement-toolbar">
      <el-form-item label-position="top" :label="t('点位')"
        ><el-select filterable v-model="pointId" :disabled="busy" @change="change">
          <el-option v-if="!points.length" value="" :label="t('暂无点位')" />
          <el-option
            v-for="p in points"
            :key="p.id"
            :value="p.id"
            :label="p.code + ' · ' + p.name + (p.enabled ? '' : t('（已停用）'))"
          /> </el-select></el-form-item
      ><el-form-item label-position="top" :label="t('时间窗口')"
        ><el-select filterable v-model="windowMinutes" @change="change">
          <el-option :value="15" :label="t('最近 15 分钟')" />
          <el-option :value="60" :label="t('最近 1 小时')" />
          <el-option :value="1440" :label="t('最近 24 小时')" /> </el-select></el-form-item
      ><el-form-item label-position="top" :label="t('粒度')"
        ><el-select filterable v-model="bucket" @change="change">
          <el-option value="raw" :label="t('原始观测')" />
          <el-option value="1m" :label="t('分钟统计')" /> </el-select></el-form-item
      ><el-checkbox class="check-label" v-model="auto">{{ t('每 5 秒刷新') }}</el-checkbox>
    </div>
    <template v-if="point">
      <div class="measurement-summary">
        <div>
          <p class="eyebrow">LATEST OBSERVED</p>
          <strong
            >{{ value(point.latestObserved?.value) }}
            <small>{{ point.canonicalUnit }}</small></strong
          >
          <p>
            <span
              class="quality-tag"
              :class="point.latestObserved?.isTrusted ? 'trusted' : 'untrusted'"
              >{{ point.latestObserved?.quality ?? t('暂无观测') }} ·
              {{
                point.latestObserved
                  ? point.latestObserved.isFresh
                    ? t('新鲜')
                    : t('陈旧')
                  : t('未接收')
              }}</span
            >
          </p>
          <p class="path-note">
            {{ point.latestObserved?.isTrusted ? t('当前可信观测') : t('不可作为当前可信值')
            }}<br />{{ t('有效时间') }} {{ at(point.latestObserved?.effectiveAt) }}
          </p>
        </div>
        <div>
          <p class="eyebrow">{{ t('LAST GOOD / 历史可信值') }}</p>
          <strong
            >{{ value(point.lastGood?.value) }} <small>{{ point.canonicalUnit }}</small></strong
          >
          <p class="path-note">
            {{
              point.lastGood
                ? point.lastGood.isFresh
                  ? t('Good · 仍在新鲜度窗口内')
                  : t('Good · 已陈旧，仅供历史参考')
                : t('尚无可信读数')
            }}<br />{{ t('有效时间') }} {{ at(point.lastGood?.effectiveAt) }}
          </p>
        </div>
        <div>
          <p class="eyebrow">{{ t('当前采集配置') }}</p>
          <p>
            {{ point.sourceKind ?? t('未启用映射') }} ·
            {{ point.enabled ? t('点位启用') : t('点位停用') }}
          </p>
          <template v-if="point.sourceKind === 'OpcUa'">
            <p role="status">{{ connectionState() }}</p>
            <p class="path-note">
              {{ t('重连') }} {{ source?.health?.reconnectCount ?? 0 }} {{ t('次 · 心跳') }}
              {{ at(source?.health?.updatedAt) }}<br />
              {{ t('协议保活') }} {{ at(source?.health?.lastKeepAliveAt) }}<br />
              {{ source?.health?.failureCode ?? '' }}<br />{{
                t('连接存活与观测新鲜度分别判断；断线期间不补造样本。')
              }}
            </p>
          </template>
          <p class="path-note">
            {{ point.sourceKey ?? '—' }}<br />{{ t('映射 v') }}{{ point.mappingVersion ?? '—' }}
            {{ t('· 点位 v') }}{{ point.version }}<br />{{ t('最后接收') }}
            {{ at(point.lastReceivedAt) }}<br />{{ t('陈旧阈值') }}
            {{ Number(point.staleAfterMs) / 1000 }} {{ t('秒') }}
          </p>
        </div>
      </div>
      <div v-if="trend" class="trend-chart">
        <div class="section-title">
          <h3>
            {{ point.name }} <small>/ {{ trend.unit }}</small>
          </h3>
          <span class="path-note">{{ quality }}</span>
        </div>
        <svg
          viewBox="0 0 960 230"
          role="img"
          :aria-label="t('{0}趋势。仅连接有效 Good 数据，坏值与缺口断线。', [point.name])"
        >
          <line x1="50" y1="195" x2="930" y2="195" stroke="#c7d4ce" />
          <line x1="50" y1="25" x2="930" y2="25" stroke="#e4ebe7" />
          <text x="8" y="30">{{ chart.max.toFixed(1) }}</text>
          <text x="8" y="198">{{ chart.min.toFixed(1) }}</text>
          <polyline
            v-for="(line, i) in chart.segments"
            :key="i"
            :points="line"
            fill="none"
            stroke="#196d58"
            stroke-width="2.5"
          />
          <circle
            v-for="(dot, i) in chart.dots"
            :key="i"
            :cx="dot.x"
            :cy="dot.y"
            r="1.8"
            fill="#196d58"
          />
          <text x="50" y="222">{{ at(trend.from) }}</text>
          <text x="930" y="222" text-anchor="end">{{ at(trend.to) }}</text>
        </svg>
        <p v-if="!chart.dots.length" class="path-note">
          {{ t('此窗口没有可连续展示的 Good 数据。分钟统计含缺口时仍可在下表查看。') }}
        </p>
        <p class="path-note">
          {{ t('Bad、Uncertain、时间异常与采样缺口均断线；没有观测的区间保持空白。')
          }}{{ trend.leadingGap || trend.trailingGap ? t('窗口边缘存在缺口。') : '' }}
          {{ t('分钟统计使用完整 UTC 分钟。页面时间采用北京时间；接口和存储为 UTC。') }}
        </p>
      </div>
      <details v-if="trend">
        <summary>{{ t('查看质量、时间与来源明细') }}</summary>
        <div class="telemetry-table">
          <el-table
            v-if="trend.bucket === 'raw'"
            :data="trend.samples.slice(-100).reverse()"
            row-key="id"
            max-height="360"
            :empty-text="t('此时间窗口暂无数据')"
            ><el-table-column :label="t('有效时间')" min-width="200"
              ><template #default="{ row: s }">
                {{ at(s.effectiveAt) }}{{ s.gapBefore ? t(' · 缺口') : ''
                }}{{ s.timeAnomaly ? t(' · 时间异常') : '' }}
              </template></el-table-column
            ><el-table-column :label="t('值 / 质量')" min-width="120"
              ><template #default="{ row: s }"
                >{{ value(s.value) }} {{ s.quality }}</template
              ></el-table-column
            ><el-table-column :label="t('时间依据')" min-width="220"
              ><template #default="{ row: s }">
                {{ s.timestampOrigin }}<br />{{ t('源') }} {{ at(s.sourceTimestamp) }}<br />{{
                  t('服务器')
                }}
                {{ at(s.serverTimestamp) }}
              </template></el-table-column
            ><el-table-column :label="t('来源 / 映射版本')" min-width="240"
              ><template #default="{ row: s }"
                >{{ s.sourceKind }} / v{{ s.mappingVersion }}<br />{{ s.sourceMappingId }}</template
              ></el-table-column
            ><el-table-column :label="t('状态 / 接收时间')" min-width="220"
              ><template #default="{ row: s }">
                {{ s.statusSymbol ?? '—' }} / {{ s.rawStatusCode ?? '—' }}<br />{{
                  at(s.receivedAt)
                }}
              </template></el-table-column
            ></el-table
          >
          <el-table
            v-else
            :data="trend.buckets.slice(-100).reverse()"
            row-key="start"
            max-height="360"
            :empty-text="t('此时间窗口暂无数据')"
            ><el-table-column :label="t('分钟')" min-width="190"
              ><template #default="{ row: b }">{{ at(b.start) }}</template></el-table-column
            ><el-table-column :label="t('最小 / 最大 / 平均')" min-width="180"
              ><template #default="{ row: b }"
                >{{ value(b.min) }} / {{ value(b.max) }} / {{ value(b.average) }}</template
              ></el-table-column
            ><el-table-column label="Good / Uncertain / Bad" min-width="170"
              ><template #default="{ row: b }"
                >{{ b.goodCount }} / {{ b.uncertainCount }} / {{ b.badCount }}</template
              ></el-table-column
            ><el-table-column :label="t('覆盖率 / 缺口')" min-width="230"
              ><template #default="{ row: b }">
                {{
                  b.coverageRatio == null
                    ? t('覆盖率未知')
                    : (Number(b.coverageRatio) * 100).toFixed(0) + '%'
                }}
                ·
                {{
                  b.samplingMode === 'Changes'
                    ? t('变化通知')
                    : b.samplingMode === 'Unknown'
                      ? t('采样区间未确认')
                      : t('周期采样')
                }}
                ·
                {{ b.hasGap ? t('有缺口') : t('完整') }}
              </template></el-table-column
            ></el-table
          >
        </div>
        <p class="path-note">{{ t('展示最近 100 条；窗口查询保留全部返回数据。') }}</p>
      </details>
    </template>
    <p v-else-if="!error" class="path-note">
      {{ busy ? t('正在读取测量…') : t('此设备尚无测量点位。') }}
    </p>
    <p v-if="checked" class="path-note">
      {{ t('最近成功读取') }} {{ at(checked) }} ·
      {{ auto && !configure ? t('自动刷新中') : t('自动刷新已暂停，当前展示为读取时的快照') }}
    </p>
    <SidePanel
      :busy="configuration?.busy"
      v-if="configure"
      :title="t('配置测量')"
      mode="dialog"
      dirty
      @close="configure = false"
    >
      <TelemetryConfiguration
        ref="configuration"
        v-if="configure"
        :asset-id="assetId"
        :plant-id="plantId"
        :points="points"
        @saved="change"
      />
    </SidePanel>
  </section>
</template>
