<script setup lang="ts">
import { locale, messageRef, t } from '../i18n'
import { formatDateTime as at } from '../time'
import {
  formatEvidenceFact,
  formatEvidenceTitle,
  formatInvestigationText,
} from '../evidencePresentation'
import SidePanel from './SidePanel.vue'
import ActionProposalPanel from './ActionProposalPanel.vue'
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { request } from '../api'

const props = defineProps<{ alarmId: string }>()
const proposalPanel = ref<InstanceType<typeof ActionProposalPanel>>()
const proposalBusy = computed(() => !!proposalPanel.value?.busy)
const proposalDirty = computed(() => !!proposalPanel.value?.dirty)
defineExpose({ busy: proposalBusy, dirty: proposalDirty })
type Evidence = {
  id: string
  kind: string
  title: string
  facts: string[]
  retrievedAt: string
  derivedTrend?: {
    algorithmVersion: string
    samples: {
      id: string
      effectiveAt: string
      value: number | null
      quality: string
      sourceVersion: number | null
      pointVersion: number | null
      mappingVersion: number
    }[]
  } | null
}
type Statement = { text: string; evidenceIds: string[] }
type PointStatus = {
  pointId: string
  code: string
  unit: string
  sourceKind: string | null
  currentTrust: string
  currentAnomaly: string
  reasons: string[]
  observation: {
    value: number | null
    quality: string
    effectiveAt: string
    receivedAt: string
    timestampOrigin: string
  } | null
  health: { state: string; isCurrent: boolean } | null
  windows: {
    kind: string
    from: string
    to: string
    retrievedAt: string
    trust: string
    goodCount: number
    badCount: number
    uncertainCount: number
    hasGap: boolean
  }[]
}
type Assessment = {
  asOf: string
  dataTrust: string
  anomalyEvidence: string
  uninspectedPointCount: number
  points: PointStatus[]
  evidenceIds: string[]
}
type Answer = {
  summary: string
  analysis?: Statement[]
  observations: Statement[]
  hypotheses: (Statement & {
    support: string
    verification: string
    basis?: string
    limitations?: string
  })[]
  nextChecks: (Statement & { record?: string; completion?: string; escalation?: string })[]
  missingInformation: string[]
}
type Run = {
  language?: 'zh-CN' | 'en'
  id: string
  assetId: string
  status: string
  createdAt: string
  completedAt: string | null
  provider: string
  model: string
  simulated: boolean
  failureCode: string | null
  result: Answer | null
  evidence: Evidence[]
  warnings: string[]
  modelRounds?: number
  toolCalls?: number
  inputTokens?: number
  outputTokens?: number
  assessment?: Assessment | null
  runRecord?: {
    leaseAttempt?: number
    usage: {
      source: string
      complete: boolean
      actualInputTokens: number | null
      actualOutputTokens: number | null
      reportedInputTokens: number
      reportedOutputTokens: number
      unknownAttempts: number
    }
  } | null
}
const runs = ref<Run[]>([]),
  selectedId = ref(''),
  busy = ref(false),
  error = messageRef()
const capabilities = ref<{ model: string; externalData: boolean } | null>(null)
const expanded = ref<Evidence | null>(null),
  evidenceError = messageRef()
const now = ref(Date.now())
const elapsed = computed(() =>
  selected.value
    ? Math.max(0, Math.floor((now.value - Date.parse(selected.value.createdAt)) / 1000))
    : 0,
)
const selected = computed(() => runs.value.find((run) => run.id === selectedId.value) ?? null)
function evidenceText(value: string) {
  return formatInvestigationText(value, selected.value?.evidence ?? [])
}
const trustLabels = computed<Record<string, string>>(() => ({
  Trusted: t('可信'),
  Partial: t('部分可信'),
  Unverifiable: t('不可验证'),
}))
const anomalyLabels = computed<Record<string, string>>(() => ({
  Present: t('存在'),
  NotFound: t('未发现'),
  Insufficient: t('证据不足'),
}))
const reasonLabels = computed<Record<string, string>>(() => ({
  ConfigurationUnverified: t('当前配置身份未核实'),
  ConfigurationVersionUnverified: t('读数的来源／测点版本未知或已变化'),
  ObservationUnavailable: t('原始观测不可查询'),
  QualityUntrusted: t('观测质量不可信'),
  ObservationUntrusted: t('观测存在可信度限制'),
  AfterAsOf: t('观测晚于取证截止'),
  StaleObservation: t('观测已陈旧'),
  ObservationTimeUnknown: t('仅有接收时间，观测时间未知'),
  MappingChanged: t('读数与当前映射不一致'),
  SourceHealthUnverified: t('来源健康缺失、过期或版本不一致'),
  SourceNotConnected: t('来源未连接'),
  SamplingContractUnknown: t('采样契约未知'),
  SilenceDoesNotProveFailure: t('按变化通知的静默不证明故障'),
  NoApplicableThreshold: t('缺少适用的启用阈值'),
  ThresholdComparisonOnly: t('仅比较当前值与启用阈值，不确认持续超限'),
  ThresholdScopeTruncated: t('启用规则范围已截断'),
}))
function reasons(point: PointStatus) {
  return point.reasons.map((reason) => reasonLabels.value[reason] ?? reason).join('；')
}
const overview = computed(() => {
  const assessment = selected.value?.assessment
  if (!assessment) return selected.value?.result ? t('调查结果已生成，请结合证据核查。') : ''
  return (
    {
      Present: t('已发现超阈值证据，原因仍需核查。'),
      NotFound: t('本次检查范围内未发现超阈值证据。'),
      Insufficient: t('当前证据不足，暂不能判断设备是否异常。'),
    }[assessment.anomalyEvidence] ?? t('请结合取证范围核查。')
  )
})
function guardWorkflowCollapse(event: MouseEvent) {
  if (proposalBusy.value || proposalDirty.value) event.preventDefault()
}
const usageNote = computed(() => {
  const record = selected.value?.runRecord
  const usage = record?.usage
  if (!usage) return t('旧记录缺少用量来源，实际消耗未知。')
  if (usage.source === 'simulated') return t('模拟运行，没有真实模型用量。')
  const prefix =
    (record?.leaseAttempt ?? 1) > 1 ? t('恢复执行：以下仅含本次执行，之前用量另存。') : ''
  if (usage.complete)
    return t('{0}模型服务报告：输入 {1}、输出 {2} tokens。', [
      prefix,
      usage.actualInputTokens,
      usage.actualOutputTokens,
    ])
  return t('{0}实际总用量未知；已报告输入 {1}、输出 {2} tokens，另有 {3} 次预留未确认。', [
    prefix,
    usage.reportedInputTokens,
    usage.reportedOutputTokens,
    usage.unknownAttempts,
  ])
})
const active = computed(
  () => selected.value && ['Pending', 'Running'].includes(selected.value.status),
)
const labels = computed<Record<string, string>>(() => ({
  Pending: t('等待调查'),
  Running: t('调查进行中'),
  Succeeded: t('调查完成'),
  Failed: t('调查未完成'),
  Cancelled: t('已取消'),
  Invalidated: t('资料或权限已变化'),
}))
const failures = computed<Record<string, string>>(() => ({
  Unavailable: t('模型服务暂时不可用，请稍后重试。'),
  Timeout: t('调查超时，未生成可靠结果。'),
  BudgetExceeded: t('本次调查已达到调用或内容上限，请查阅已有证据。'),
  Truncated: t('模型响应达到输出上限且内容不完整，未展示结论，请查阅已有证据。'),
  InvalidResponse: t('模型输出未通过事实或引用校验，未展示该答案。'),
  InvalidRequest: t('模型提出了不允许的工具调用，调查已停止。'),
  AuthorizationChanged: t('调查期间读取权限或资料已变化，请重新核查。'),
  InvalidConfiguration: t('调查服务与模型配置不一致，请联系管理员。'),
  NotConfigured: t('尚未配置模型连接，请联系管理员。'),
  LeaseExpired: t('调查执行中断且无法恢复，请重新开始。'),
}))
let generation = 0,
  disposed = false
async function refresh() {
  if (busy.value || disposed) return
  const current = ++generation
  try {
    const items = await request<Run[]>(`/alarms/${props.alarmId}/investigations`)
    if (disposed || current !== generation) return
    runs.value = items
    if (!items.some((run) => run.id === selectedId.value)) selectedId.value = items[0]?.id ?? ''
    // Hide old excerpts when polling reports invalidation; expansion authorizes each fresh read.
    if (selected.value?.status === 'Invalidated') expanded.value = null
    error.value = ''
  } catch (e) {
    if (disposed || current !== generation) return
    runs.value = []
    expanded.value = null
    error.value = () => (e instanceof Error ? e.message : t('调查记录暂不可读。'))
  }
}
async function start() {
  if (busy.value || !capabilities.value) return
  busy.value = true
  error.value = ''
  expanded.value = null
  generation++
  try {
    const run = await request<Run>(
      `/alarms/${props.alarmId}/investigations?language=${locale.value}`,
      'POST',
    )
    if (disposed) return
    runs.value = [run, ...runs.value.filter((item) => item.id !== run.id)]
    selectedId.value = run.id
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('无法开始调查。'))
  } finally {
    busy.value = false
  }
}
async function cancel() {
  const run = selected.value
  if (!run || busy.value) return
  busy.value = true
  generation++
  try {
    const stopped = await request<Run>(`/investigations/${run.id}/cancel`, 'POST')
    if (!disposed) runs.value = runs.value.map((item) => (item.id === stopped.id ? stopped : item))
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('取消失败，请刷新状态。'))
  } finally {
    busy.value = false
  }
}
async function expand(id: string) {
  const runId = selected.value?.id
  if (!runId) return
  expanded.value = null
  evidenceError.value = ''
  try {
    const item = await request<Evidence>(
      `/investigations/${runId}/evidence/${encodeURIComponent(id)}`,
    )
    if (!disposed && selected.value?.id === runId) expanded.value = item
  } catch (e) {
    evidenceError.value = () => (e instanceof Error ? e.message : t('证据不可读，请重新调查。'))
    void refresh()
  }
}
function choose() {
  expanded.value = null
  evidenceError.value = ''
}
onMounted(async () => {
  try {
    capabilities.value = await request('/investigation-capabilities')
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('调查服务暂不可用。'))
  }
  await refresh()
})
const timer = setInterval(() => void refresh(), 3000)
const clockTimer = setInterval(() => {
  now.value = Date.now()
}, 1000)
onUnmounted(() => {
  disposed = true
  generation++
  clearInterval(timer)
  clearInterval(clockTimer)
})
</script>

<template>
  <section class="investigation-panel" :aria-label="t('告警只读调查')">
    <div class="section-title">
      <div>
        <h3>{{ t('AI 调查') }}</h3>
      </div>
      <div class="actions">
        <el-button
          :disabled="busy || proposalBusy || proposalDirty || !capabilities || !!active"
          @click="start"
          type="primary"
        >
          {{ active ? t('正在调查…') : t('开始只读调查') }}
        </el-button>
        <el-button v-if="active" :disabled="busy" @click="cancel">{{ t('取消调查') }}</el-button>
      </div>
    </div>
    <p class="path-note">
      {{
        !capabilities
          ? t('正在读取调查服务配置。')
          : capabilities.externalData
            ? t('外部模型：{0}。将发送本设备必要的告警、趋势、维护摘要和授权资料节选。', [
                capabilities.model,
              ])
            : t('当前使用模拟模型，不向外部发送业务资料。')
      }}
      {{ t('调查只读取资料；告警确认、维修与创建工单仍由人员操作。') }}
    </p>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <el-form-item v-if="runs.length" :label="t('我的调查记录')"
      ><el-select
        filterable
        v-model="selectedId"
        :disabled="proposalBusy || proposalDirty"
        @change="choose"
      >
        <el-option
          v-for="run in runs"
          :key="run.id"
          :value="run.id"
          :label="at(run.createdAt) + ' · ' + (labels[run.status] ?? run.status)"
        />
      </el-select>
    </el-form-item>
    <p v-else class="path-note">{{ t('尚无调查记录。开始后可在此查看进度与引用。') }}</p>
    <template v-if="selected">
      <p class="path-note">
        {{
          t('调查语言：{0}。事实可随界面切换语言，原文可在证据详情查看。', [
            selected.language === 'en' ? 'English' : '简体中文',
          ])
        }}
        <span v-if="(selected.language ?? 'zh-CN') !== locale">
          {{ t('如需用当前语言生成分析建议，请重新发起调查；事实与证据展示可直接切换语言。') }}
        </span>
      </p>
      <p class="investigation-status" role="status">
        <strong>{{ labels[selected.status] ?? selected.status }}</strong
        ><span
          >{{
            selected.simulated
              ? t('模拟结果 · 不代表真实模型诊断')
              : t('模型 {0}', [selected.model])
          }}
          · {{ at(selected.createdAt) }} {{ t('的调查快照') }}</span
        >
      </p>
      <p v-if="active" class="path-note">
        {{ t('已等待') }} {{ elapsed }} {{ t('秒 · 已获取') }} {{ selected.evidence.length }}
        {{ t('项证据。') }}
        {{
          selected.status === 'Pending'
            ? t('任务正在排队。')
            : selected.evidence.length
              ? t('正在等待模型完成分析与结果校验，完成后显示答案。')
              : t('正在收集授权证据。')
        }}
        {{ t('等待期间可查看已获取的证据，也可取消调查。') }}
      </p>
      <p
        v-if="selected.failureCode && selected.status === 'Failed'"
        class="error-banner"
        role="alert"
      >
        {{ failures[selected.failureCode] ?? t('调查未完成，请核查已有证据后重试。') }}
      </p>
      <article v-if="overview" class="investigation-overview" :aria-label="t('调查概览')">
        <h4>{{ overview }}</h4>
        <div v-if="selected.assessment" class="assessment-tags">
          <el-tag :type="selected.assessment.dataTrust === 'Trusted' ? 'success' : 'warning'">
            {{ t('数据：{0}', [trustLabels[selected.assessment.dataTrust] ?? t('不可验证')]) }}
          </el-tag>
          <el-tag :type="selected.assessment.anomalyEvidence === 'Present' ? 'danger' : 'info'">
            {{ t('异常证据：')
            }}{{ anomalyLabels[selected.assessment.anomalyEvidence] ?? t('证据不足') }}
          </el-tag>
        </div>
        <p v-if="selected.assessment" class="path-note">
          {{ t('截至') }} {{ at(selected.assessment.asOf) }} {{ t('· 已检查') }}
          {{ selected.assessment.points.length }} {{ t('个测点')
          }}<span v-if="selected.assessment.uninspectedPointCount"
            >{{ t('，另有') }} {{ selected.assessment.uninspectedPointCount }}
            {{ t('个启用测点未检查') }}</span
          >。
          <span v-if="selected.assessment.dataTrust !== 'Trusted'">{{
            t('部分测点或覆盖范围尚未核实。')
          }}</span>
          {{ t('这是取证快照，不代表此刻状态，也不确认根因或设备安全。') }}
        </p>
        <p v-else class="path-note">
          {{ t('旧调查未记录双维度判断，请核对下方原始事实与限制。') }}
        </p>
        <p v-if="selected.warnings.length" class="path-note">
          {{ t('含') }} {{ selected.warnings.length }}
          {{ t('项数据或范围提示，可在“事实、依据与限制”中核对。') }}
        </p>
      </article>
      <article v-if="selected.result?.analysis?.length" :aria-label="t('趋势分析')">
        <h4>{{ t('趋势分析') }}</h4>
        <div
          v-for="(item, index) in selected.result.analysis"
          :key="index"
          class="investigation-statement"
        >
          <p>{{ evidenceText(item.text) }}</p>
          <el-button
            v-for="id in item.evidenceIds"
            :key="id"
            link
            type="primary"
            class="citation"
            @click="expand(id)"
          >
            {{ t('查看依据') }} {{ id }}
          </el-button>
        </div>
      </article>
      <article v-if="selected.result" class="investigation-next" :aria-label="t('下一步核查')">
        <h4>{{ t('下一步核查') }}</h4>
        <p v-if="!selected.result.nextChecks.length" class="path-note">
          {{ t('暂无有依据的核查建议，请先查看证据与缺项。') }}
        </p>
        <ol class="next-checks">
          <li v-for="(item, index) in selected.result.nextChecks" :key="index">
            <p>{{ evidenceText(item.text) }}</p>
            <details class="check-details">
              <summary>{{ t('查看依据与执行条件') }}</summary>
              <p v-if="item.record">{{ t('需记录：') }}{{ evidenceText(item.record) }}</p>
              <p v-if="item.completion">{{ t('完成条件：') }}{{ evidenceText(item.completion) }}</p>
              <p v-if="item.escalation">{{ t('升级条件：') }}{{ evidenceText(item.escalation) }}</p>
              <el-button
                v-for="id in item.evidenceIds"
                :key="id"
                link
                type="primary"
                class="citation"
                @click="expand(id)"
                >{{ t('查看依据') }} {{ id }}</el-button
              >
            </details>
          </li>
        </ol>
      </article>
      <details class="investigation-disclosure" :key="selected.id + ':evidence'">
        <summary>
          {{ t('事实、依据与限制') }}
          <span class="disclosure-count">{{ selected.evidence.length }} {{ t('项证据') }}</span>
        </summary>
        <article v-if="selected.assessment" :aria-label="t('数据可信度与异常证据')">
          <h4>{{ t('测点与时间窗口') }}</h4>
          <p class="path-note">
            {{ t('截至') }}
            {{ at(selected.assessment.asOf) }}
            {{ t('的取证快照，不代表页面此刻状态。仅限下列测点与启用阈值；未检查启用测点') }}
            {{ selected.assessment.uninspectedPointCount }}
            {{ t('个。历史窗口单列；这些判断不确认根因、持续超限或设备安全。') }}
          </p>
          <div
            v-for="(point, index) in selected.assessment.points"
            :key="point.pointId"
            class="investigation-statement"
          >
            <p>
              <strong>{{ point.code }}</strong
              >：{{ trustLabels[point.currentTrust] ?? t('不可验证') }}{{ t('／异常判断：')
              }}{{ anomalyLabels[point.currentAnomaly] ?? t('证据不足') }}{{ t('；观测') }}
              {{ point.observation?.value ?? t('无值') }} {{ point.unit }}{{ t('，质量') }}
              {{ point.observation?.quality ?? t('未知') }}。
            </p>
            <p class="path-note">
              {{ reasons(point) }}{{ t('。来源') }} {{ point.sourceKind ?? t('未知')
              }}{{ t('；来源健康') }} {{ point.health?.state ?? t('未知')
              }}{{
                point.health && !point.health.isCurrent ? t('（记录已过期或版本不一致）') : ''
              }}。
            </p>
            <p v-if="point.observation" class="path-note">
              {{ t('有效时间') }} {{ at(point.observation.effectiveAt) }}{{ t('；接收时间') }}
              {{ at(point.observation.receivedAt) }}{{ t('；时间来源') }}
              {{ point.observation.timestampOrigin }}。
            </p>
            <details v-if="point.windows.length">
              <summary>{{ t('历史及完成分钟窗口的质量与覆盖') }}</summary>
              <p class="path-note">
                {{
                  t(
                    '分钟投影未保留逐样本的完整来源版本，因此有值窗口仍仅为部分核实，不能据此认定连续趋势。',
                  )
                }}
              </p>
              <p v-for="window in point.windows" :key="window.kind" class="path-note">
                {{ at(window.from) }} {{ t('至') }} {{ at(window.to)
                }}{{ t('（含起始、不含结束）：') }}{{ trustLabels[window.trust] ?? t('不可验证')
                }}{{ t('；可信') }} {{ window.goodCount }}，Bad {{ window.badCount }}，Uncertain
                {{ window.uncertainCount }}；{{ window.hasGap ? t('存在缺口') : t('未记录缺口')
                }}{{ t('；查询时间') }} {{ at(window.retrievedAt)
                }}{{ t('。仅代表已入库完成分钟。') }}
              </p>
            </details>
            <el-button
              v-if="selected.assessment.evidenceIds[index]"
              type="primary"
              class="secondary citation"
              @click="expand(selected.assessment.evidenceIds[index]!)"
              >{{ t('查看测点依据') }} {{ selected.assessment.evidenceIds[index] }}</el-button
            >
          </div>
        </article>
        <div v-if="selected.result" class="investigation-columns">
          <article>
            <h4>{{ t('有据可查的事实') }}</h4>
            <p v-if="!selected.result.observations.length">{{ t('本次未生成事实结论。') }}</p>
            <div
              v-for="(item, index) in selected.result.observations"
              :key="index"
              class="investigation-statement"
            >
              <p>{{ evidenceText(item.text) }}</p>
              <el-button
                v-for="id in item.evidenceIds"
                :key="id"
                class="secondary citation"
                @click="expand(id)"
                type="primary"
              >
                {{ t('查看证据') }} {{ id }}
              </el-button>
            </div>
          </article>
          <article v-if="selected.result.hypotheses.length">
            <h4>{{ t('待验证假设') }}</h4>
            <div
              v-for="(item, index) in selected.result.hypotheses"
              :key="index"
              class="investigation-statement"
            >
              <p>{{ evidenceText(item.text) }}</p>
              <p v-if="item.basis">{{ t('支持依据：') }}{{ evidenceText(item.basis) }}</p>
              <p v-if="item.limitations">
                {{ t('证据不足：') }}{{ evidenceText(item.limitations) }}
              </p>
              <small
                >{{ t('证据支持：') }}{{ item.support === 'moderate' ? t('中等') : t('有限')
                }}{{ t('。核实方式：') }}{{ evidenceText(item.verification) }}</small
              >
              <div>
                <el-button
                  v-for="id in item.evidenceIds"
                  :key="id"
                  class="secondary citation"
                  @click="expand(id)"
                  type="primary"
                >
                  {{ t('查看依据') }} {{ id }}
                </el-button>
              </div>
            </div>
          </article>
          <article v-if="selected.result.missingInformation.length">
            <h4>{{ t('仍缺少的信息') }}</h4>
            <ul>
              <li v-for="item in selected.result.missingInformation" :key="item">
                {{ evidenceText(item) }}
              </li>
            </ul>
          </article>
        </div>
        <article v-if="selected.warnings.length">
          <h4>{{ t('数据与范围提示') }}</h4>
          <ul class="investigation-warnings">
            <li v-for="warning in selected.warnings" :key="warning">
              {{ formatEvidenceFact(warning, 'DataQuality') }}
            </li>
          </ul>
        </article>
        <details v-if="selected.evidence.length">
          <summary>{{ t('本次已获取的证据（{0}）', [selected.evidence.length]) }}</summary>
          <div class="actions">
            <el-button
              v-for="item in selected.evidence"
              :key="item.id"
              class="secondary citation"
              @click="expand(item.id)"
              type="primary"
            >
              {{ item.id }} · {{ formatEvidenceTitle(item) }}
            </el-button>
          </div>
        </details>
        <details v-if="selected.modelRounds != null" class="run-details">
          <summary>{{ t('调用与用量记录') }}</summary>
          <p class="path-note">
            {{ t('模型') }} {{ selected.modelRounds }} {{ t('轮，读取') }} {{ selected.toolCalls }}
            {{ t('次；预算核算输入') }} {{ selected.inputTokens }}{{ t('、输出') }}
            {{ selected.outputTokens }} {{ t('tokens（含未确认预留）。') }}{{ usageNote }}
          </p>
        </details>
      </details>
      <details
        v-if="['Succeeded', 'Invalidated'].includes(selected.status)"
        class="investigation-disclosure maintenance-disclosure"
        :key="selected.id + ':maintenance'"
      >
        <summary @click="guardWorkflowCollapse">
          {{ t('安排维护') }}
          <span class="disclosure-count">{{
            proposalDirty ? t('有未保存内容') : proposalBusy ? t('处理中') : t('整理工单或追加备注')
          }}</span>
        </summary>
        <ActionProposalPanel
          v-if="['Succeeded', 'Invalidated'].includes(selected.status)"
          :key="selected.id"
          ref="proposalPanel"
          :run-id="selected.id"
          :asset-id="selected.assetId"
          :available="selected.status === 'Succeeded'"
          @evidence="expand"
        />
      </details>
      <p v-if="evidenceError" class="error-banner" role="alert">{{ evidenceError }}</p>
      <SidePanel
        v-if="expanded"
        mode="dialog"
        :title="expanded.id + ' · ' + formatEvidenceTitle(expanded)"
        :subtitle="t('AI 调查 / 授权证据')"
        @close="expanded = null"
      >
        <aside class="investigation-evidence">
          <div class="section-title">
            <h4>{{ expanded.id }} · {{ formatEvidenceTitle(expanded) }}</h4>
            <el-button @click="expanded = null">{{ t('收起') }}</el-button>
          </div>
          <p class="path-note">
            {{ t('取数时间') }} {{ at(expanded.retrievedAt) }}
            {{ t('· 本次展开已重新检查读取权限') }}
          </p>
          <p v-for="(fact, index) in expanded.facts" :key="index" class="evidence-text">
            {{ formatEvidenceFact(fact, expanded.kind) }}
          </p>
          <details v-if="expanded.derivedTrend" class="original-evidence">
            <summary>{{ t('核对计算样本') }} · {{ expanded.derivedTrend.samples.length }}</summary>
            <p>{{ t('算法版本') }}：{{ expanded.derivedTrend.algorithmVersion }}</p>
            <el-table :data="expanded.derivedTrend.samples" row-key="id" max-height="320">
              <el-table-column prop="id" :label="t('样本标识')" min-width="280" />
              <el-table-column :label="t('有效时间')" min-width="180">
                <template #default="{ row }">{{ at(row.effectiveAt) }}</template>
              </el-table-column>
              <el-table-column prop="value" :label="t('观测值')" min-width="90" />
              <el-table-column prop="quality" :label="t('质量')" min-width="90" />
              <el-table-column prop="sourceVersion" :label="t('来源版本')" min-width="100" />
              <el-table-column prop="pointVersion" :label="t('测点版本')" min-width="100" />
              <el-table-column prop="mappingVersion" :label="t('映射版本')" min-width="100" />
            </el-table>
          </details>
          <details v-if="locale === 'en'" class="original-evidence">
            <summary>{{ t('查看原始证据') }}</summary>
            <h5>{{ expanded.title }}</h5>
            <p v-for="(fact, index) in expanded.facts" :key="index" class="evidence-text">
              {{ fact }}
            </p>
          </details>
        </aside>
      </SidePanel>
    </template>
  </section>
</template>

<style scoped>
.investigation-panel {
  margin-top: 24px;
  padding: 20px;
  background: #f3f5f1;
  border: 1px solid var(--line);
  border-radius: 8px;
}
.investigation-panel .section-title h3 {
  margin: 0;
}
.investigation-status {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  padding-block: 12px;
  border-block: 1px solid var(--line);
}
.investigation-status span {
  font-size: 13px;
  color: var(--muted);
}
.investigation-overview {
  padding: 4px 0 12px;
}
.investigation-overview h4 {
  margin: 8px 0 12px;
  font-size: 16px;
  line-height: 1.6;
}
.assessment-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.investigation-next {
  padding-block: 4px 12px;
}
.investigation-next h4 {
  margin: 8px 0;
}
.next-checks {
  margin: 0;
  padding-left: 24px;
}
.next-checks > li {
  padding: 4px 0 12px 4px;
}
.next-checks > li::marker {
  color: var(--muted);
  font-weight: 600;
}
.next-checks p {
  margin: 4px 0 8px;
  line-height: 1.7;
  overflow-wrap: anywhere;
}
.check-details {
  font-size: 13px;
  color: var(--el-text-color-regular);
}
.check-details summary {
  color: var(--el-color-primary);
}
.investigation-disclosure {
  margin-top: 12px;
  padding: 12px 16px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--el-bg-color);
}
.investigation-disclosure > summary {
  font-weight: 600;
  line-height: 1.7;
}
.investigation-panel summary {
  cursor: pointer;
  padding-block: 4px;
}
.investigation-panel summary:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 4px;
  border-radius: 2px;
}
.disclosure-count {
  margin-left: 8px;
  font-size: 12px;
  font-weight: 400;
  color: var(--muted);
}
.investigation-disclosure[open] > summary {
  margin-bottom: 12px;
}
.investigation-disclosure article + article {
  margin-top: 20px;
}
.investigation-warnings {
  padding-left: 20px;
  line-height: 1.7;
  font-size: 13px;
}
.investigation-warnings li + li {
  margin-top: 8px;
}
.investigation-columns {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 16px;
}
.investigation-columns article {
  min-width: 0;
}
.investigation-statement {
  padding-bottom: 12px;
  overflow-wrap: anywhere;
}
.citation {
  font-size: 13px;
  margin: 4px 8px 4px 0;
}
.run-details {
  margin-top: 16px;
}
.investigation-evidence {
  padding: 16px;
  background: var(--el-bg-color);
  border: 1px solid var(--line);
}
.evidence-text {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.investigation-panel details .actions {
  flex-wrap: wrap;
}
@media (max-width: 760px) {
  .investigation-panel {
    padding: 16px;
  }
  .investigation-disclosure {
    padding: 10px 12px;
  }
  .disclosure-count {
    display: block;
    margin-left: 18px;
  }
}
</style>
