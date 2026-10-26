<script setup lang="ts">
import SidePanel from './SidePanel.vue'
import ActionProposalPanel from './ActionProposalPanel.vue'
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { request } from '../api'

const props = defineProps<{ alarmId: string }>()
const proposalPanel = ref<InstanceType<typeof ActionProposalPanel>>()
const proposalBusy = computed(() => !!proposalPanel.value?.busy)
const proposalDirty = computed(() => !!proposalPanel.value?.dirty)
defineExpose({ busy: proposalBusy, dirty: proposalDirty })
type Evidence = { id: string; kind: string; title: string; facts: string[]; retrievedAt: string }
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
  error = ref('')
const capabilities = ref<{ model: string; externalData: boolean } | null>(null)
const expanded = ref<Evidence | null>(null),
  evidenceError = ref('')
const now = ref(Date.now())
const elapsed = computed(() =>
  selected.value
    ? Math.max(0, Math.floor((now.value - Date.parse(selected.value.createdAt)) / 1000))
    : 0,
)
const selected = computed(() => runs.value.find((run) => run.id === selectedId.value) ?? null)
const trustLabels: Record<string, string> = {
  Trusted: '可信',
  Partial: '部分可信',
  Unverifiable: '不可验证',
}
const anomalyLabels: Record<string, string> = {
  Present: '存在',
  NotFound: '未发现',
  Insufficient: '证据不足',
}
const reasonLabels: Record<string, string> = {
  ConfigurationUnverified: '当前配置身份未核实',
  ConfigurationVersionUnverified: '读数的来源／测点版本未知或已变化',
  ObservationUnavailable: '原始观测不可查询',
  QualityUntrusted: '观测质量不可信',
  ObservationUntrusted: '观测存在可信度限制',
  AfterAsOf: '观测晚于取证截止',
  StaleObservation: '观测已陈旧',
  ObservationTimeUnknown: '仅有接收时间，观测时间未知',
  MappingChanged: '读数与当前映射不一致',
  SourceHealthUnverified: '来源健康缺失、过期或版本不一致',
  SourceNotConnected: '来源未连接',
  SamplingContractUnknown: '采样契约未知',
  SilenceDoesNotProveFailure: '按变化通知的静默不证明故障',
  NoApplicableThreshold: '缺少适用的启用阈值',
  ThresholdComparisonOnly: '仅比较当前值与启用阈值，不确认持续超限',
  ThresholdScopeTruncated: '启用规则范围已截断',
}
function reasons(point: PointStatus) {
  return point.reasons.map((reason) => reasonLabels[reason] ?? reason).join('；')
}
const overview = computed(() => {
  const assessment = selected.value?.assessment
  if (!assessment) return selected.value?.result ? '调查结果已生成，请结合证据核查。' : ''
  return (
    {
      Present: '已发现超阈值证据，原因仍需核查。',
      NotFound: '本次检查范围内未发现超阈值证据。',
      Insufficient: '当前证据不足，暂不能判断设备是否异常。',
    }[assessment.anomalyEvidence] ?? '请结合取证范围核查。'
  )
})
function guardWorkflowCollapse(event: MouseEvent) {
  if (proposalBusy.value || proposalDirty.value) event.preventDefault()
}
const usageNote = computed(() => {
  const record = selected.value?.runRecord
  const usage = record?.usage
  if (!usage) return '旧记录缺少用量来源，实际消耗未知。'
  if (usage.source === 'simulated') return '模拟运行，没有真实模型用量。'
  const prefix = (record?.leaseAttempt ?? 1) > 1 ? '恢复执行：以下仅含本次执行，之前用量另存。' : ''
  if (usage.complete)
    return `${prefix}模型服务报告：输入 ${usage.actualInputTokens}、输出 ${usage.actualOutputTokens} tokens。`
  return `${prefix}实际总用量未知；已报告输入 ${usage.reportedInputTokens}、输出 ${usage.reportedOutputTokens} tokens，另有 ${usage.unknownAttempts} 次预留未确认。`
})
const active = computed(
  () => selected.value && ['Pending', 'Running'].includes(selected.value.status),
)
const labels: Record<string, string> = {
  Pending: '等待调查',
  Running: '调查进行中',
  Succeeded: '调查完成',
  Failed: '调查未完成',
  Cancelled: '已取消',
  Invalidated: '资料或权限已变化',
}
const failures: Record<string, string> = {
  Unavailable: '模型服务暂时不可用，请稍后重试。',
  Timeout: '调查超时，未生成可靠结果。',
  BudgetExceeded: '本次调查已达到调用或内容上限，请查阅已有证据。',
  Truncated: '模型响应达到输出上限且内容不完整，未展示结论，请查阅已有证据。',
  InvalidResponse: '模型输出未通过事实或引用校验，未展示该答案。',
  InvalidRequest: '模型提出了不允许的工具调用，调查已停止。',
  AuthorizationChanged: '调查期间读取权限或资料已变化，请重新核查。',
  InvalidConfiguration: '调查服务与模型配置不一致，请联系管理员。',
  NotConfigured: '尚未配置模型连接，请联系管理员。',
  LeaseExpired: '调查执行中断且无法恢复，请重新开始。',
}
let generation = 0,
  disposed = false
function at(value: string) {
  return new Date(value).toLocaleString('zh-CN', { hour12: false })
}
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
    error.value = e instanceof Error ? e.message : '调查记录暂不可读。'
  }
}
async function start() {
  if (busy.value || !capabilities.value) return
  busy.value = true
  error.value = ''
  expanded.value = null
  generation++
  try {
    const run = await request<Run>(`/alarms/${props.alarmId}/investigations`, 'POST')
    if (disposed) return
    runs.value = [run, ...runs.value.filter((item) => item.id !== run.id)]
    selectedId.value = run.id
  } catch (e) {
    error.value = e instanceof Error ? e.message : '无法开始调查。'
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
    error.value = e instanceof Error ? e.message : '取消失败，请刷新状态。'
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
    evidenceError.value = e instanceof Error ? e.message : '证据不可读，请重新调查。'
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
    error.value = e instanceof Error ? e.message : '调查服务暂不可用。'
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
  <section class="investigation-panel" aria-label="告警只读调查">
    <div class="section-title">
      <div>
        <h3>AI 调查</h3>
      </div>
      <div class="actions">
        <el-button
          :disabled="busy || proposalBusy || proposalDirty || !capabilities || !!active"
          @click="start"
          type="primary"
        >
          {{ active ? '正在调查…' : '开始只读调查' }}
        </el-button>
        <el-button v-if="active" :disabled="busy" @click="cancel">取消调查</el-button>
      </div>
    </div>
    <p class="path-note">
      {{
        !capabilities
          ? '正在读取调查服务配置。'
          : capabilities.externalData
            ? `外部模型：${capabilities.model}。将发送本设备必要的告警、趋势、维护摘要和授权资料节选。`
            : '当前使用模拟模型，不向外部发送业务资料。'
      }}
      调查只读取资料；告警确认、维修与创建工单仍由人员操作。
    </p>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <el-form-item v-if="runs.length" label="我的调查记录"
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
    <p v-else class="path-note">尚无调查记录。开始后可在此查看进度与引用。</p>
    <template v-if="selected">
      <p class="investigation-status" role="status">
        <strong>{{ labels[selected.status] ?? selected.status }}</strong
        ><span
          >{{ selected.simulated ? '模拟结果 · 不代表真实模型诊断' : `模型 ${selected.model}` }} ·
          {{ at(selected.createdAt) }} 的调查快照</span
        >
      </p>
      <p v-if="active" class="path-note">
        已等待 {{ elapsed }} 秒 · 已获取 {{ selected.evidence.length }} 项证据。
        {{
          selected.status === 'Pending'
            ? '任务正在排队。'
            : selected.evidence.length
              ? '正在等待模型完成分析与结果校验，完成后显示答案。'
              : '正在收集授权证据。'
        }}
        等待期间可查看已获取的证据，也可取消调查。
      </p>
      <p
        v-if="selected.failureCode && selected.status === 'Failed'"
        class="error-banner"
        role="alert"
      >
        {{ failures[selected.failureCode] ?? '调查未完成，请核查已有证据后重试。' }}
      </p>
      <article v-if="overview" class="investigation-overview" aria-label="调查概览">
        <h4>{{ overview }}</h4>
        <div v-if="selected.assessment" class="assessment-tags">
          <el-tag :type="selected.assessment.dataTrust === 'Trusted' ? 'success' : 'warning'">
            数据{{ trustLabels[selected.assessment.dataTrust] ?? '不可验证' }}
          </el-tag>
          <el-tag :type="selected.assessment.anomalyEvidence === 'Present' ? 'danger' : 'info'">
            异常证据：{{ anomalyLabels[selected.assessment.anomalyEvidence] ?? '证据不足' }}
          </el-tag>
        </div>
        <p v-if="selected.assessment" class="path-note">
          截至 {{ at(selected.assessment.asOf) }} · 已检查
          {{ selected.assessment.points.length }} 个测点<span
            v-if="selected.assessment.uninspectedPointCount"
            >，另有 {{ selected.assessment.uninspectedPointCount }} 个启用测点未检查</span
          >。
          <span v-if="selected.assessment.dataTrust !== 'Trusted'"
            >部分测点或覆盖范围尚未核实。</span
          >
          这是取证快照，不代表此刻状态，也不确认根因或设备安全。
        </p>
        <p v-else class="path-note">旧调查未记录双维度判断，请核对下方原始事实与限制。</p>
        <p v-if="selected.warnings.length" class="path-note">
          含 {{ selected.warnings.length }} 项数据或范围提示，可在“事实、依据与限制”中核对。
        </p>
      </article>
      <article v-if="selected.result" class="investigation-next" aria-label="下一步核查">
        <h4>下一步核查</h4>
        <p v-if="!selected.result.nextChecks.length" class="path-note">
          暂无有依据的核查建议，请先查看证据与缺项。
        </p>
        <ol class="next-checks">
          <li v-for="(item, index) in selected.result.nextChecks" :key="index">
            <p>{{ item.text }}</p>
            <details class="check-details">
              <summary>查看依据与执行条件</summary>
              <p v-if="item.record">需记录：{{ item.record }}</p>
              <p v-if="item.completion">完成条件：{{ item.completion }}</p>
              <p v-if="item.escalation">升级条件：{{ item.escalation }}</p>
              <el-button
                v-for="id in item.evidenceIds"
                :key="id"
                link
                type="primary"
                class="citation"
                @click="expand(id)"
                >查看依据 {{ id }}</el-button
              >
            </details>
          </li>
        </ol>
      </article>
      <details class="investigation-disclosure" :key="selected.id + ':evidence'">
        <summary>
          事实、依据与限制
          <span class="disclosure-count">{{ selected.evidence.length }} 项证据</span>
        </summary>
        <article v-if="selected.assessment" aria-label="数据可信度与异常证据">
          <h4>测点与时间窗口</h4>
          <p class="path-note">
            截至
            {{ at(selected.assessment.asOf) }}
            的取证快照，不代表页面此刻状态。仅限下列测点与启用阈值；未检查启用测点
            {{ selected.assessment.uninspectedPointCount }}
            个。历史窗口单列；这些判断不确认根因、持续超限或设备安全。
          </p>
          <div
            v-for="(point, index) in selected.assessment.points"
            :key="point.pointId"
            class="investigation-statement"
          >
            <p>
              <strong>{{ point.code }}</strong
              >：{{ trustLabels[point.currentTrust] ?? '不可验证' }}／异常判断：{{
                anomalyLabels[point.currentAnomaly] ?? '证据不足'
              }}；观测 {{ point.observation?.value ?? '无值' }} {{ point.unit }}，质量
              {{ point.observation?.quality ?? '未知' }}。
            </p>
            <p class="path-note">
              {{ reasons(point) }}。来源 {{ point.sourceKind ?? '未知' }}；来源健康
              {{ point.health?.state ?? '未知'
              }}{{ point.health && !point.health.isCurrent ? '（记录已过期或版本不一致）' : '' }}。
            </p>
            <p v-if="point.observation" class="path-note">
              有效时间 {{ at(point.observation.effectiveAt) }}；接收时间
              {{ at(point.observation.receivedAt) }}；时间来源
              {{ point.observation.timestampOrigin }}。
            </p>
            <details v-if="point.windows.length">
              <summary>历史及完成分钟窗口的质量与覆盖</summary>
              <p class="path-note">
                分钟投影未保留逐样本的完整来源版本，因此有值窗口仍仅为部分核实，不能据此认定连续趋势。
              </p>
              <p v-for="window in point.windows" :key="window.kind" class="path-note">
                {{ at(window.from) }} 至 {{ at(window.to) }}：{{
                  trustLabels[window.trust] ?? '不可验证'
                }}；可信 {{ window.goodCount }}，Bad {{ window.badCount }}，Uncertain
                {{ window.uncertainCount }}；{{
                  window.hasGap ? '存在缺口' : '未记录缺口'
                }}；查询时间 {{ at(window.retrievedAt) }}。仅代表已入库完成分钟。
              </p>
            </details>
            <el-button
              v-if="selected.assessment.evidenceIds[index]"
              type="primary"
              class="secondary citation"
              @click="expand(selected.assessment.evidenceIds[index]!)"
              >查看测点依据 {{ selected.assessment.evidenceIds[index] }}</el-button
            >
          </div>
        </article>
        <div v-if="selected.result" class="investigation-columns">
          <article>
            <h4>有据可查的事实</h4>
            <p v-if="!selected.result.observations.length">本次未生成事实结论。</p>
            <div
              v-for="(item, index) in selected.result.observations"
              :key="index"
              class="investigation-statement"
            >
              <p>{{ item.text }}</p>
              <el-button
                v-for="id in item.evidenceIds"
                :key="id"
                class="secondary citation"
                @click="expand(id)"
                type="primary"
              >
                查看证据 {{ id }}
              </el-button>
            </div>
          </article>
          <article v-if="selected.result.hypotheses.length">
            <h4>待验证假设</h4>
            <div
              v-for="(item, index) in selected.result.hypotheses"
              :key="index"
              class="investigation-statement"
            >
              <p>{{ item.text }}</p>
              <p v-if="item.basis">支持依据：{{ item.basis }}</p>
              <p v-if="item.limitations">证据不足：{{ item.limitations }}</p>
              <small
                >证据支持：{{ item.support === 'moderate' ? '中等' : '有限' }}。核实方式：{{
                  item.verification
                }}</small
              >
              <div>
                <el-button
                  v-for="id in item.evidenceIds"
                  :key="id"
                  class="secondary citation"
                  @click="expand(id)"
                  type="primary"
                >
                  查看依据 {{ id }}
                </el-button>
              </div>
            </div>
          </article>
          <article v-if="selected.result.missingInformation.length">
            <h4>仍缺少的信息</h4>
            <ul>
              <li v-for="item in selected.result.missingInformation" :key="item">{{ item }}</li>
            </ul>
          </article>
        </div>
        <article v-if="selected.warnings.length">
          <h4>数据与范围提示</h4>
          <ul class="investigation-warnings">
            <li v-for="warning in selected.warnings" :key="warning">{{ warning }}</li>
          </ul>
        </article>
        <details v-if="selected.evidence.length">
          <summary>本次已获取的证据（{{ selected.evidence.length }}）</summary>
          <div class="actions">
            <el-button
              v-for="item in selected.evidence"
              :key="item.id"
              class="secondary citation"
              @click="expand(item.id)"
              type="primary"
            >
              {{ item.id }} · {{ item.title }}
            </el-button>
          </div>
        </details>
        <details v-if="selected.modelRounds != null" class="run-details">
          <summary>调用与用量记录</summary>
          <p class="path-note">
            模型 {{ selected.modelRounds }} 轮，读取 {{ selected.toolCalls }} 次；预算核算输入
            {{ selected.inputTokens }}、输出 {{ selected.outputTokens }} tokens（含未确认预留）。{{
              usageNote
            }}
          </p>
        </details>
      </details>
      <details
        v-if="['Succeeded', 'Invalidated'].includes(selected.status)"
        class="investigation-disclosure maintenance-disclosure"
        :key="selected.id + ':maintenance'"
      >
        <summary @click="guardWorkflowCollapse">
          安排维护
          <span class="disclosure-count">{{
            proposalDirty ? '有未保存内容' : proposalBusy ? '处理中' : '整理工单或追加备注'
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
        :title="expanded.id + ' · ' + expanded.title"
        subtitle="AI 调查 / 授权证据"
        @close="expanded = null"
      >
        <aside class="investigation-evidence">
          <div class="section-title">
            <h4>{{ expanded.id }} · {{ expanded.title }}</h4>
            <el-button @click="expanded = null">收起</el-button>
          </div>
          <p class="path-note">
            取数时间 {{ at(expanded.retrievedAt) }} · 本次展开已重新检查读取权限
          </p>
          <p v-for="(fact, index) in expanded.facts" :key="index" class="evidence-text">
            {{ fact }}
          </p>
        </aside>
      </SidePanel>
    </template>
  </section>
</template>

<style scoped>
.investigation-panel {
  margin-top: 24px;
  padding: 20px;
  background: var(--el-fill-color-extra-light);
  border: 1px solid var(--el-border-color-light);
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
  border-block: 1px solid var(--el-border-color-light);
}
.investigation-status span {
  font-size: 13px;
  color: var(--el-text-color-secondary);
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
  color: var(--el-text-color-secondary);
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
  border: 1px solid var(--el-border-color-light);
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
  color: var(--el-text-color-secondary);
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
  border: 1px solid var(--el-border-color-light);
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
