<script setup lang="ts">
import { messageRef, t } from '../i18n'
import { formatDateTime as at, formatEvidenceText } from '../time'
import { formatEvidenceFact, formatEvidenceTitle } from '../evidencePresentation'
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { ApiError, request } from '../api'
import { useSession } from '../stores/session'
import { useWorkOrderDrawer } from '../stores/workOrderDrawer'
import type { FormInstance } from 'element-plus'

const props = withDefaults(
  defineProps<{
    runId: string
    available: boolean
    assetId?: string
    canCreateWorkOrder?: boolean
  }>(),
  { canCreateWorkOrder: true },
)
const emit = defineEmits<{ evidence: [id: string] }>()
type Draft = {
  title: string
  description: string
  priority: string
  assignedToUserId: string | null
}
type Proposal = {
  id: string
  aiRunId: string
  plantId: string
  assetId: string
  alarmId: string
  assetName: string
  status: string
  version: number
  payloadHash: string
  expiresAt: string
  draft: Draft | null
  actionType: string
  noteDraft: { text: string } | null
  targetWorkOrderId: string | null
  targetWorkOrderVersion: number | null
  targetWorkOrderNumber: string | null
  targetWorkOrderTitle: string | null
  resultEntityId: string | null
  executedAt: string | null
  approvalCorrelationId: string | null
  unavailableReason: string | null
  investigation: {
    simulated: boolean
    model: string
    warnings: string[]
    evidence: { id: string; title: string; kind: string; facts: string[] }[]
  } | null
}
const session = useSession()
const workOrderDrawer = useWorkOrderDrawer()
const formRef = ref<FormInstance>()
const rules = computed(() => ({
  title: [
    {
      required: true,
      whitespace: true,
      max: 200,
      message: t('请填写标题，最多200字。'),
      trigger: 'blur',
    },
  ],
  description: [{ max: 8000, message: t('描述最多8000字。'), trigger: 'blur' }],
}))
const canMaintain = computed(
  () =>
    session.user?.roles.some((role) => ['Admin', 'MaintenanceEngineer'].includes(role)) ?? false,
)
const proposal = ref<Proposal | null>(null)
const recoveryKey = `plantops-proposal-execution:${props.runId}`
function readExecution() {
  try {
    return globalThis.sessionStorage?.getItem(recoveryKey) ?? ''
  } catch {
    return ''
  }
}
function rememberExecution(id: string | null) {
  try {
    if (id) globalThis.sessionStorage?.setItem(recoveryKey, id)
    else globalThis.sessionStorage?.removeItem(recoveryKey)
  } catch {
    /* Storage may be unavailable; the server receipt remains authoritative. */
  }
}
const selectedId = ref(readExecution())
const items = ref<Proposal[]>([])
const form = ref<Draft>({ title: '', description: '', priority: 'Medium', assignedToUserId: null })
const saved = messageRef(),
  editVersion = ref(0),
  review = ref(false)
const busy = ref(false),
  loading = ref(false),
  error = messageRef(),
  uncertain = ref(!!selectedId.value)
const assignees = ref<{ id: string; displayName: string }[]>([])
type TargetOrder = { id: string; number: string; title: string; status: string; version: number }
const targets = ref<TargetOrder[]>([])
const targetId = ref(''),
  targetCursor = ref<string | null>(null),
  targetsLoaded = ref(false)
const isNote = computed(() => proposal.value?.actionType === 'AppendWorkOrderNote')
async function loadTargets(more = false) {
  if (busy.value || uncertain.value || dirty.value || !canMaintain.value || !props.assetId) return
  busy.value = true
  generation++
  error.value = ''
  try {
    const page = await request<{ items: TargetOrder[]; nextCursor: string | null }>(
      `/work-orders?assetId=${props.assetId}&limit=50${more && targetCursor.value ? `&cursor=${encodeURIComponent(targetCursor.value)}` : ''}`,
    )
    if (disposed) return
    targets.value = [...(more ? targets.value : []), ...page.items].filter(
      (order) => !['Closed', 'Cancelled'].includes(order.status),
    )
    targetCursor.value = page.nextCursor
    targetsLoaded.value = true
    if (!more) targetId.value = ''
  } catch (e) {
    targets.value = []
    targetId.value = ''
    targetsLoaded.value = false
    error.value = () => (e instanceof Error ? e.message : t('无法读取可追加备注的工单。'))
  } finally {
    busy.value = false
  }
}
const dirty = computed(
  () => proposal.value?.status === 'Pending' && JSON.stringify(form.value) !== saved.value,
)
const changed = computed(
  () => proposal.value?.status === 'Pending' && proposal.value.version !== editVersion.value,
)
const now = ref(Date.now())
const expired = computed(
  () => !!proposal.value && now.value >= Date.parse(proposal.value.expiresAt),
)
const editable = computed(
  () =>
    canMaintain.value &&
    proposal.value?.status === 'Pending' &&
    !expired.value &&
    !uncertain.value &&
    !changed.value,
)
const labels = computed<Record<string, string>>(() => ({
  Pending: t('待审批提案'),
  Executed: t('已审批并执行'),
  Rejected: t('已拒绝'),
  Expired: t('已过期'),
  Invalidated: t('已失效'),
}))
let disposed = false
let reading = false
let generation = 0
function accept(value: Proposal, reset = false) {
  const revised =
    proposal.value &&
    (value.version !== proposal.value.version || value.payloadHash !== proposal.value.payloadHash)
  const preserve = !reset && dirty.value && value.status === 'Pending'
  proposal.value = value
  selectedId.value = value.id
  items.value = [value, ...items.value.filter((item) => item.id !== value.id)]
  if (!preserve) {
    form.value = value.draft
      ? { ...value.draft }
      : {
          title: '',
          description: value.noteDraft?.text ?? '',
          priority: 'Medium',
          assignedToUserId: null,
        }
    saved.value = JSON.stringify(form.value)
    editVersion.value = value.version
  }
  if (value.status !== 'Pending' || changed.value || revised) review.value = false
}
async function read(background = false) {
  if (busy.value || reading || disposed) return
  reading = true
  const currentGeneration = ++generation
  if (!background) loading.value = true
  try {
    const values = await request<Proposal[]>(`/investigations/${props.runId}/proposals`)
    if (disposed || busy.value || currentGeneration !== generation) return
    items.value = values
    const current = selectedId.value
      ? (values.find((value) => value.id === selectedId.value) ??
        (await request<Proposal>(`/action-proposals/${selectedId.value}`)))
      : values[0]
    if (disposed || busy.value || currentGeneration !== generation) return
    if (current) accept(current)
    error.value = ''
    uncertain.value = false
    rememberExecution(null)
  } catch (e) {
    if (disposed || currentGeneration !== generation) return
    error.value = () => (e instanceof Error ? e.message : t('提案暂不可读。'))
    // A failed authorization read must not leave an old draft or confirmation visible.
    review.value = false
    proposal.value = null
    items.value = []
    form.value = { title: '', description: '', priority: 'Medium', assignedToUserId: null }
  } finally {
    reading = false
    loading.value = false
  }
}
async function choose(id: string) {
  const item = items.value.find((value) => value.id === id)
  if (item) accept(item, true)
  review.value = false
  await loadAssignees()
}
async function loadAssignees() {
  if (!proposal.value) return
  try {
    assignees.value = await request(`/plants/${proposal.value.plantId}/maintenance-users`)
  } catch {
    assignees.value = []
  }
}
async function create(actionType = 'CreateWorkOrder') {
  if (
    busy.value ||
    uncertain.value ||
    dirty.value ||
    proposal.value?.status === 'Pending' ||
    !canMaintain.value ||
    !props.available ||
    (actionType === 'CreateWorkOrder' && props.canCreateWorkOrder === false)
  )
    return
  const target = targets.value.find((order) => order.id === targetId.value)
  if (actionType === 'AppendWorkOrderNote' && !target) {
    error.value = () => t('请选择目标工单。')
    return
  }
  busy.value = true
  generation++
  error.value = ''
  try {
    const value =
      actionType === 'AppendWorkOrderNote'
        ? await request<Proposal>(`/investigations/${props.runId}/note-proposals`, 'POST', {
            workOrderId: target!.id,
            workOrderVersion: target!.version,
          })
        : await request<Proposal>(`/investigations/${props.runId}/proposals`, 'POST')
    if (disposed) return
    accept(value, true)
    review.value = false
    uncertain.value = false
    if (value.actionType !== 'AppendWorkOrderNote') await loadAssignees()
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('无法整理草稿。'))
  } finally {
    busy.value = false
  }
}
async function save() {
  if (busy.value || !editable.value || !proposal.value) return
  if (formRef.value && !(await formRef.value.validate().catch(() => false))) return
  if (
    (!isNote.value && (!form.value.title.trim() || form.value.title.length > 200)) ||
    (isNote.value && !form.value.description.trim()) ||
    form.value.description.length > 8000
  ) {
    error.value = () =>
      isNote.value ? t('备注必填且最多8000字。') : t('请填写标题（最多200字），描述最多8000字。')
    return
  }
  busy.value = true
  generation++
  error.value = ''
  try {
    const value = await request<Proposal>(
      `/action-proposals/${proposal.value.id}${isNote.value ? '/note' : ''}`,
      'PATCH',
      {
        version: editVersion.value,
        draft: isNote.value ? { text: form.value.description } : form.value,
      },
    )
    if (disposed) return
    accept(value, true)
    review.value = value.status === 'Pending'
  } catch (e) {
    review.value = false
    error.value = () => (e instanceof Error ? e.message : t('保存失败，输入已保留。'))
  } finally {
    busy.value = false
  }
}
async function reject() {
  if (busy.value || !editable.value || !proposal.value) return
  busy.value = true
  generation++
  error.value = ''
  try {
    const value = await request<Proposal>(`/action-proposals/${proposal.value.id}/reject`, 'POST', {
      version: proposal.value.version,
    })
    if (!disposed) accept(value, true)
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('拒绝失败，请刷新状态。'))
  } finally {
    busy.value = false
  }
}
async function execute() {
  const current = proposal.value
  if (!current || busy.value || !editable.value || !review.value || dirty.value) return
  busy.value = true
  generation++
  error.value = ''
  rememberExecution(current.id)
  try {
    const value = await request<Proposal>(`/action-proposals/${current.id}/execute`, 'POST', {
      version: current.version,
      payloadHash: current.payloadHash,
      idempotencyKey: `${current.id}:${current.version}:${current.payloadHash.slice(0, 16)}`,
    })
    if (!disposed) {
      accept(value, true)
      rememberExecution(null)
    }
  } catch (e) {
    review.value = false
    uncertain.value = true
    error.value = () =>
      e instanceof ApiError && e.status === 409
        ? t('提案内容、状态或依据已变化，请先查询原提案并重新核对。')
        : t('执行结果待核实。请查询原提案，确认操作是否已完成后再操作。')
  } finally {
    busy.value = false
  }
}
function openReceipt() {
  const value = proposal.value
  const orderId =
    value?.actionType === 'AppendWorkOrderNote' ? value.targetWorkOrderId : value?.resultEntityId
  if (value && orderId) workOrderDrawer.open({ assetId: value.assetId, orderId })
}
onMounted(async () => {
  await read()
  await loadAssignees()
})
const timer = setInterval(() => {
  now.value = Date.now()
  void read(true)
}, 5000)
onUnmounted(() => {
  disposed = true
  clearInterval(timer)
})
defineExpose({ busy, dirty })
</script>

<template>
  <section class="proposal-panel" :aria-label="t('调查工单提案')" v-loading="loading">
    <div class="section-title">
      <h4>{{ t('根据调查安排维护') }}</h4>
      <el-button
        v-if="canMaintain && available && canCreateWorkOrder !== false"
        type="primary"
        :disabled="busy || dirty || uncertain || proposal?.status === 'Pending'"
        @click="create()"
        >{{ t('整理工单草稿') }}</el-button
      >
    </div>
    <p class="path-note">
      {{
        t(
          '复用本次调查整理提案，不再调用模型。保存提案后需核对完整内容并确认，才会创建工单或追加备注。',
        )
      }}
    </p>
    <p v-if="!canMaintain" class="path-note">
      {{ t('仅工厂管理员和维护工程师可整理与审批工单提案。') }}
    </p>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <div v-if="canMaintain && available && proposal?.status !== 'Pending'" class="actions">
      <el-button :disabled="busy || dirty || uncertain" @click="loadTargets()">{{
        t('选择已有工单追加备注')
      }}</el-button>
      <template v-if="targetsLoaded">
        <el-select
          v-model="targetId"
          :aria-label="t('追加备注的目标工单')"
          :placeholder="t('选择本设备的可维护工单')"
          :disabled="busy || uncertain"
        >
          <el-option
            v-for="order in targets"
            :key="order.id"
            :value="order.id"
            :label="`${order.title} · ${order.number} · v${order.version}`"
          />
        </el-select>
        <el-button v-if="targetCursor" :disabled="busy || uncertain" @click="loadTargets(true)">{{
          t('加载更多工单')
        }}</el-button>
        <el-button
          type="primary"
          :disabled="busy || uncertain || !targetId"
          @click="create('AppendWorkOrderNote')"
          >{{ t('整理备注提案') }}</el-button
        >
        <p v-if="!targets.length" class="path-note">
          {{ t('当前页没有可追加备注的工单。已关闭或取消的工单不可选择。') }}
        </p>
      </template>
    </div>
    <div class="actions">
      <el-select
        v-if="items.length"
        :model-value="proposal?.id"
        :aria-label="t('我的工单提案')"
        :disabled="busy || dirty || uncertain"
        @change="choose"
      >
        <el-option
          v-for="item in items"
          :key="item.id"
          :value="item.id"
          :label="`${item.actionType === 'AppendWorkOrderNote' ? t('追加备注') : t('创建工单')} · ${labels[item.status] ?? item.status} · ${at(item.expiresAt)}`"
        />
      </el-select>
      <el-button :disabled="busy || (dirty && !uncertain)" @click="read()">{{
        uncertain ? t('查询原提案执行结果') : t('刷新提案')
      }}</el-button>
    </div>
    <template v-if="proposal">
      <p role="status">
        <strong>{{ labels[proposal.status] ?? proposal.status }}</strong> · {{ proposal.assetName }}
        {{ t('· 有效期至') }} {{ at(proposal.expiresAt) }}
      </p>
      <p v-if="isNote" class="path-note">
        {{ t('目标工单：') }}{{ proposal.targetWorkOrderTitle }} ·
        {{ proposal.targetWorkOrderNumber }}<br />{{ t('审批绑定版本：v')
        }}{{ proposal.targetWorkOrderVersion }} {{ t('· 工单') }}
        {{ proposal.targetWorkOrderId }}
      </p>
      <p class="path-note">
        {{ t('关联设备：') }}{{ proposal.assetId }}<br />{{ t('关联告警：') }}{{ proposal.alarmId
        }}<br />{{ t('调查记录：') }}{{ proposal.aiRunId }}
      </p>
      <el-alert
        v-if="proposal.unavailableReason"
        :title="proposal.unavailableReason"
        type="warning"
        :closable="false"
      />
      <p v-if="expired && proposal.status === 'Pending'" role="alert">
        {{ t('提案已到期，请刷新后重新整理草稿。') }}
      </p>
      <p v-if="changed" role="alert">
        {{ t('提案已在其他会话修改。当前输入已保留，请核对后放弃本地输入并重新载入。') }}
      </p>
      <el-button v-if="changed" :disabled="busy" @click="accept(proposal, true)">{{
        t('放弃本地输入并载入最新提案')
      }}</el-button>
      <template
        v-if="proposal.status === 'Pending' && (proposal.draft || proposal.noteDraft) && !uncertain"
      >
        <el-form
          v-if="!review"
          ref="formRef"
          :model="form"
          :rules="rules"
          label-position="top"
          @submit.prevent="save"
        >
          <el-form-item v-if="!isNote" :label="t('标题（必填）')" prop="title"
            ><el-input v-model="form.title" maxlength="200" :disabled="!editable || busy"
          /></el-form-item>
          <el-form-item :label="isNote ? t('追加备注（必填）') : t('描述')" prop="description"
            ><el-input
              v-model="form.description"
              type="textarea"
              :rows="12"
              maxlength="8000"
              show-word-limit
              :disabled="!editable || busy"
          /></el-form-item>
          <el-form-item v-if="!isNote" :label="t('优先级（模板默认为中）')"
            ><el-select v-model="form.priority" :disabled="!editable || busy"
              ><el-option value="Low" :label="t('低')" /><el-option
                value="Medium"
                :label="t('中')" /><el-option value="High" :label="t('高')" /></el-select
          ></el-form-item>
          <el-form-item v-if="!isNote" :label="t('责任人')"
            ><el-select
              v-model="form.assignedToUserId"
              clearable
              :disabled="!editable || busy"
              :placeholder="t('暂不分配')"
              ><el-option :value="null" :label="t('暂不分配')" /><el-option
                v-for="user in assignees"
                :key="user.id"
                :value="user.id"
                :label="user.displayName" /></el-select
          ></el-form-item>
          <div class="actions">
            <el-button native-type="submit" type="primary" :disabled="!editable || busy">{{
              t('保存并核对完整内容')
            }}</el-button
            ><el-button :disabled="!editable || busy || dirty" @click="reject">{{
              t('拒绝提案')
            }}</el-button>
          </div>
        </el-form>
        <article v-else class="proposal-confirmation" :aria-label="t('确认维护提案完整内容')">
          <h4>
            {{ isNote ? t('请核对目标工单与完整备注后确认追加') : t('请核对后确认创建工单') }}
          </h4>
          <p>
            {{
              isNote
                ? t('将追加一条备注。目标工单版本发生变化时，提案失效，需重新整理。')
                : t('创建后为草稿工单，后续打开、执行与关闭沿用现有工单流程。')
            }}
          </p>
          <h5 v-if="proposal.draft">{{ proposal.draft.title }}</h5>
          <p class="proposal-description">
            {{ formatEvidenceText(proposal.noteDraft?.text ?? proposal.draft?.description ?? '') }}
          </p>
          <p v-if="proposal.draft">
            {{ t('优先级：')
            }}{{
              { Low: t('低'), Medium: t('中'), High: t('高') }[proposal.draft.priority] ??
              proposal.draft.priority
            }}<br />{{ t('责任人：')
            }}{{
              assignees.find((user) => user.id === proposal?.draft?.assignedToUserId)
                ?.displayName ??
              proposal.draft.assignedToUserId ??
              t('暂不分配')
            }}
          </p>
          <p class="path-note">
            {{ t('依据：')
            }}{{
              proposal.investigation?.simulated ? t('模拟模型调查') : proposal.investigation?.model
            }}{{ t('。以下提示与引用在确认时仍会重新检查。') }}
          </p>
          <ul>
            <li v-for="warning in proposal.investigation?.warnings ?? []" :key="warning">
              {{ formatEvidenceFact(warning, 'DataQuality') }}
            </li>
          </ul>
          <div
            v-for="item in proposal.investigation?.evidence.filter(
              (item) => item.kind === 'DataQuality',
            ) ?? []"
            :key="item.id"
          >
            <p v-for="fact in item.facts" :key="fact">{{ formatEvidenceFact(fact, item.kind) }}</p>
          </div>
          <div class="actions">
            <el-button
              v-for="item in proposal.investigation?.evidence ?? []"
              :key="item.id"
              @click="emit('evidence', item.id)"
              >{{ t('查看') }} {{ item.id }} · {{ formatEvidenceTitle(item) }}</el-button
            >
          </div>
          <div class="actions">
            <el-button
              type="primary"
              :disabled="!editable || busy || dirty"
              :loading="busy"
              @click="execute"
              >{{ isNote ? t('确认追加备注') : t('确认创建工单') }}</el-button
            ><el-button :disabled="busy" @click="review = false">{{ t('返回编辑') }}</el-button
            ><el-button :disabled="!editable || busy" @click="reject">{{
              t('拒绝提案')
            }}</el-button>
          </div>
        </article>
      </template>
      <div
        v-if="proposal.status === 'Executed' && proposal.resultEntityId"
        class="proposal-receipt"
        role="status"
      >
        <p>
          {{ isNote ? t('备注已追加，无需重复审批。') : t('工单已创建，无需重复审批。')
          }}{{ proposal.executedAt ? at(proposal.executedAt) : '' }}
        </p>
        <p v-if="isNote" class="path-note">{{ t('备注编号：') }}{{ proposal.resultEntityId }}</p>
        <el-button type="primary" link @click="openReceipt">
          {{ isNote ? t('查看已追加备注的工单 →') : t('查看已创建的关联工单 →') }}
        </el-button>
        <p class="path-note">
          {{ t('提案') }} {{ proposal.id }} {{ t('· 审批追踪') }}
          {{ proposal.approvalCorrelationId }}
        </p>
      </div>
    </template>
  </section>
</template>

<style scoped>
.proposal-panel {
  margin-top: 24px;
  padding-top: 24px;
  border-top: 1px solid #d4d9cc;
}
.proposal-description {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.proposal-confirmation,
.proposal-receipt {
  padding: 16px;
  background: white;
  border: 1px solid #d4d9cc;
}
.actions {
  flex-wrap: wrap;
  margin-block: 16px;
}
</style>
