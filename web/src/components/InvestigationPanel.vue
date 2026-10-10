<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { request } from '../api'

const props = defineProps<{ alarmId: string }>()
type Evidence = { id: string; kind: string; title: string; facts: string[]; retrievedAt: string }
type Statement = { text: string; evidenceIds: string[] }
type Answer = {
  summary: string
  observations: Statement[]
  hypotheses: (Statement & { support: string; verification: string })[]
  nextChecks: Statement[]
  missingInformation: string[]
}
type Run = {
  id: string
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
}
const runs = ref<Run[]>([]),
  selectedId = ref(''),
  busy = ref(false),
  error = ref('')
const capabilities = ref<{ model: string; externalData: boolean } | null>(null)
const expanded = ref<Evidence | null>(null),
  evidenceError = ref('')
const selected = computed(() => runs.value.find((run) => run.id === selectedId.value) ?? null)
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
onUnmounted(() => {
  disposed = true
  generation++
  clearInterval(timer)
})
</script>

<template>
  <section class="investigation-panel" aria-label="告警只读调查">
    <div class="section-title">
      <div>
        <p class="eyebrow">INVESTIGATION / EVIDENCE</p>
        <h3>让判断回到证据。</h3>
      </div>
      <div class="actions">
        <button :disabled="busy || !capabilities || !!active" @click="start">
          {{ active ? '正在调查…' : '开始只读调查' }}
        </button>
        <button v-if="active" class="secondary" :disabled="busy" @click="cancel">取消调查</button>
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
    <p v-if="error" class="error-banner" role="alert">{{ error }}</p>
    <label v-if="runs.length"
      >我的调查记录
      <select v-model="selectedId" @change="choose">
        <option v-for="run in runs" :key="run.id" :value="run.id">
          {{ at(run.createdAt) }} · {{ labels[run.status] ?? run.status }}
        </option>
      </select>
    </label>
    <p v-else class="path-note">尚无调查记录。开始后可在此查看进度与引用。</p>
    <template v-if="selected">
      <p class="investigation-status" role="status">
        <strong>{{ labels[selected.status] ?? selected.status }}</strong
        ><span
          >{{ selected.simulated ? '模拟结果 · 不代表真实模型诊断' : `模型 ${selected.model}` }} ·
          {{ at(selected.createdAt) }} 的调查快照</span
        >
      </p>
      <p
        v-if="selected.failureCode && selected.status === 'Failed'"
        class="error-banner"
        role="alert"
      >
        {{ failures[selected.failureCode] ?? '调查未完成，请核查已有证据后重试。' }}
      </p>
      <ul v-if="selected.warnings.length" class="investigation-warnings">
        <li v-for="warning in selected.warnings" :key="warning">{{ warning }}</li>
      </ul>
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
            <button
              v-for="id in item.evidenceIds"
              :key="id"
              class="secondary citation"
              @click="expand(id)"
            >
              查看证据 {{ id }}
            </button>
          </div>
        </article>
        <article>
          <h4>待验证假设</h4>
          <p v-if="!selected.result.hypotheses.length">证据不足，暂不推断原因。</p>
          <div
            v-for="(item, index) in selected.result.hypotheses"
            :key="index"
            class="investigation-statement"
          >
            <p>{{ item.text }}</p>
            <small
              >证据支持：{{ item.support === 'moderate' ? '中等' : '有限' }}。核实方式：{{
                item.verification
              }}</small
            >
            <div>
              <button
                v-for="id in item.evidenceIds"
                :key="id"
                class="secondary citation"
                @click="expand(id)"
              >
                查看依据 {{ id }}
              </button>
            </div>
          </div>
        </article>
        <article>
          <h4>建议核查</h4>
          <div
            v-for="(item, index) in selected.result.nextChecks"
            :key="index"
            class="investigation-statement"
          >
            <p>{{ item.text }}</p>
            <button
              v-for="id in item.evidenceIds"
              :key="id"
              class="secondary citation"
              @click="expand(id)"
            >
              查看依据 {{ id }}
            </button>
          </div>
        </article>
        <article>
          <h4>仍缺少的信息</h4>
          <ul>
            <li v-for="item in selected.result.missingInformation" :key="item">{{ item }}</li>
          </ul>
          <p v-if="!selected.result.missingInformation.length">
            仍需现场人员核查，不能据此确认根因或设备安全。
          </p>
        </article>
      </div>
      <details v-if="selected.evidence.length">
        <summary>本次已获取的证据（{{ selected.evidence.length }}）</summary>
        <div class="actions">
          <button
            v-for="item in selected.evidence"
            :key="item.id"
            class="secondary citation"
            @click="expand(item.id)"
          >
            {{ item.id }} · {{ item.title }}
          </button>
        </div>
      </details>
      <p v-if="evidenceError" class="error-banner" role="alert">{{ evidenceError }}</p>
      <aside v-if="expanded" class="investigation-evidence" aria-live="polite">
        <div class="section-title">
          <h4>{{ expanded.id }} · {{ expanded.title }}</h4>
          <button class="secondary" @click="expanded = null">收起</button>
        </div>
        <p class="path-note">
          取数时间 {{ at(expanded.retrievedAt) }} · 本次展开已重新检查读取权限
        </p>
        <p v-for="(fact, index) in expanded.facts" :key="index" class="evidence-text">{{ fact }}</p>
      </aside>
    </template>
  </section>
</template>

<style scoped>
.investigation-panel {
  margin-top: 2rem;
  padding: 1.5rem;
  background: #f4f5ef;
  border: 1px solid #d4d9cc;
  border-top: 3px solid #53623c;
}
.investigation-status {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem 1.5rem;
  padding-block: 0.9rem;
  border-block: 1px solid #d4d9cc;
}
.investigation-status span {
  font-size: 0.85rem;
  color: #62685b;
}
.investigation-warnings {
  padding: 1rem 1rem 1rem 2rem;
  background: #fff8df;
  border-left: 3px solid #b78b26;
}
.investigation-columns {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 1.5rem;
}
.investigation-columns article {
  min-width: 0;
}
.investigation-statement {
  padding-bottom: 1rem;
  overflow-wrap: anywhere;
}
.citation {
  font-size: 0.8rem;
  padding: 0.4rem 0.65rem;
  margin: 0.2rem 0.35rem 0.2rem 0;
}
.investigation-evidence {
  padding: 1rem;
  margin-top: 1rem;
  background: white;
  border: 1px solid #d4d9cc;
}
.evidence-text {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
@media (max-width: 760px) {
  .investigation-columns {
    grid-template-columns: 1fr;
  }
  .investigation-panel {
    padding: 1rem;
  }
}
</style>
