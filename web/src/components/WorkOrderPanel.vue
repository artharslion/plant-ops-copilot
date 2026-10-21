<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useWorkOrderDrawer } from '../stores/workOrderDrawer'
import { useLeaveProtection } from '../composables/useLeaveProtection'
import type { FormInstance } from 'element-plus'
import { ApiError, request } from '../api'
import { useSession } from '../stores/session'
import { subscribePlantEvents } from '../realtime'
const props = withDefaults(
  defineProps<{
    assetId: string
    plantId: string
    archived?: boolean
    history?: boolean
    active?: boolean
    initialAlarmId?: string
    detailId?: string
    assetLabel?: string
    embedded?: boolean
  }>(),
  { active: true },
)
type Order = {
  id: string
  number: string
  title: string
  description: string
  priority: string
  status: string
  version: number
  alarmId: string | null
  assignedToUserId: string | null
  resolution: string | null
  createdAt: string
  completedAt: string | null
  closedAt: string | null
}
type Note = { id: string; text: string; kind: string; actorUserId: string; occurredAt: string }
const session = useSession()
const drawer = useWorkOrderDrawer()
const emit = defineEmits<{ created: [id: string] }>()
const createForm = ref<FormInstance>()
const createModel = computed(() => ({ title: title.value }))
function openOrder(id: string) {
  drawer.open({ assetId: props.assetId, orderId: id, alarmId: props.initialAlarmId })
}
const writer = computed(() =>
  session.user?.roles.some((x) => ['Admin', 'MaintenanceEngineer'].includes(x)),
)
const orders = ref<Order[]>([]),
  selected = ref<Order | null>(null),
  notes = ref<Note[]>([])
const users = ref<{ id: string; displayName: string }[]>([]),
  alarms = ref<{ id: string; state: string; ruleName: string; startedAt: string }[]>([])
const cursor = ref<string | null>(null),
  busy = ref(false),
  error = ref(''),
  notice = ref(''),
  creating = ref(false)
const title = ref(''),
  description = ref(''),
  priority = ref('Medium'),
  assigned = ref(''),
  alarmId = ref(''),
  reason = ref(''),
  resolution = ref(''),
  note = ref('')
const labels: Record<string, string> = {
  Draft: '草稿',
  Open: '待执行',
  InProgress: '执行中',
  Completed: '已完成，待复核',
  Closed: '已关闭',
  Cancelled: '已取消',
}
const steps = ['创建任务', '执行维护', '提交结果']
const stepIndex = computed(
  () =>
    ({ Draft: 0, Open: 1, InProgress: 1, Completed: 2, Closed: 2 })[
      selected.value?.status ?? 'Draft'
    ] ?? -1,
)
const primaryAction = computed(
  () =>
    ({
      Draft: { status: 'Open', label: '打开工单' },
      Open: { status: 'InProgress', label: '开始执行' },
      InProgress: { status: 'Completed', label: '完成并关闭' },
      Completed: { status: 'Closed', label: '复核并关闭' },
    })[selected.value?.status ?? ''],
)
const canCancel = computed(() =>
  ['Draft', 'Open', 'InProgress'].includes(selected.value?.status ?? ''),
)
const dirty = computed(() =>
  creating.value
    ? !!(
        title.value ||
        description.value ||
        assigned.value ||
        alarmId.value ||
        priority.value !== 'Medium'
      )
    : !!(note.value || reason.value || resolution.value !== (selected.value?.resolution ?? '')),
)
if (props.detailId && !props.embedded) {
  useLeaveProtection(
    () => dirty.value,
    () => busy.value,
  )
}
defineExpose({ busy, dirty })
const changed = ref(false)
const checking = ref(false)
const nextHints: Record<string, string> = {
  Draft: '任务已保存为草稿，准备好后打开工单即可开始安排维护。',
  Open: '任务已创建，准备就绪后点击“开始执行”。',
  InProgress: '维护结束后填写处理结果，一次提交完成并关闭；需要他人复核时可仅提交完成。',
  Completed: '处理结果已提交，等待复核；确认无误后关闭，需要补充处理时可退回返工。',
  Closed: '维护流程已结束，可查看处理结果与历史记录。',
  Cancelled: '工单已取消，后续处理请创建新的工单。',
}
async function load(more = false, background = false) {
  if (!background) busy.value = true
  error.value = ''
  try {
    const page = await request<{ items: Order[]; nextCursor: string | null }>(
      `/work-orders?assetId=${props.assetId}&history=${!!props.history}&limit=50${more && cursor.value ? `&cursor=${encodeURIComponent(cursor.value)}` : ''}`,
    )
    orders.value = more ? [...orders.value, ...page.items] : page.items
    cursor.value = page.nextCursor
  } catch (e) {
    orders.value = []
    error.value = e instanceof Error ? e.message : '加载失败。'
  } finally {
    if (!background) busy.value = false
  }
}
async function select(w: Order, preserve = false) {
  busy.value = true
  error.value = ''
  if (!preserve) {
    selected.value = null
    notes.value = []
  }
  try {
    const [detail, history] = await Promise.all([
      request<Order>(`/work-orders/${w.id}`),
      request<Note[]>(`/work-orders/${w.id}/notes`),
    ])
    selected.value = detail
    creating.value = false
    notes.value = history
    changed.value = false
    reason.value = ''
    resolution.value = selected.value.resolution ?? ''
    note.value = ''
  } catch (e) {
    error.value = e instanceof Error ? e.message : '加载失败。'
  } finally {
    busy.value = false
  }
}
async function prepare() {
  busy.value = true
  error.value = ''
  selected.value = null
  try {
    users.value = await request(`/plants/${props.plantId}/maintenance-users`)
    alarms.value = (
      await request<{ items: typeof alarms.value }>(
        `/alarms?plantId=${props.plantId}&assetId=${props.assetId}&limit=200`,
      )
    ).items
    alarmId.value = alarms.value.some((x) => x.id === props.initialAlarmId)
      ? props.initialAlarmId!
      : ''
    title.value = ''
    description.value = ''
    priority.value = 'Medium'
    assigned.value = ''
    creating.value = true
  } catch (e) {
    error.value = e instanceof Error ? e.message : '加载失败。'
  } finally {
    busy.value = false
  }
}
function submitCreate(event: Event) {
  const submitter = (event as SubmitEvent).submitter as HTMLButtonElement | null
  void create(submitter?.value !== 'draft')
}
async function create(openImmediately = true) {
  if (busy.value) return
  if (createForm.value && !(await createForm.value.validate().catch(() => false))) return
  busy.value = true
  error.value = ''
  notice.value = ''
  try {
    const w = await request<Order>('/work-orders', 'POST', {
      assetId: props.assetId,
      openImmediately,
      title: title.value,
      description: description.value,
      priority: priority.value,
      assignedToUserId: assigned.value || null,
      alarmId: alarmId.value || null,
    })
    selected.value = w
    creating.value = false
    title.value = ''
    description.value = ''
    if (!props.detailId) await load()
    await select(w, true)
    notice.value = openImmediately
      ? '工单已创建并打开，可以开始执行。'
      : '草稿已保存，可稍后打开工单。'
    busy.value = false
    emit('created', w.id)
  } catch (e) {
    error.value = e instanceof Error ? e.message : '创建失败。'
  } finally {
    busy.value = false
  }
}
async function mutate(status?: string, closeAfterCompletion = false) {
  if (!selected.value || busy.value || changed.value) return
  if (status && note.value.trim()) {
    error.value = '请先保存或清空维护备注，再提交状态变更。'
    return
  }
  busy.value = true
  error.value = ''
  notice.value = ''
  try {
    const w = await request<Order>(
      `/work-orders/${selected.value.id}/${status ? 'transition' : 'notes'}`,
      'POST',
      status
        ? {
            version: selected.value.version,
            status,
            closeAfterCompletion,
            reason: reason.value || null,
            resolution: resolution.value || null,
          }
        : { version: selected.value.version, text: note.value },
    )
    selected.value = w
    if (!props.detailId) await load()
    await select(w, true)
    notice.value = closeAfterCompletion ? '处理结果已保存，工单已完成并关闭。' : '维护记录已保存。'
  } catch (e) {
    error.value = e instanceof Error ? e.message : '保存失败。'
    if (e instanceof ApiError && e.status === 409) changed.value = true
  } finally {
    busy.value = false
  }
}
onMounted(async () => {
  if (props.detailId === 'new') await prepare()
  else if (props.detailId) await select({ id: props.detailId } as Order)
  else await load()
})
async function refreshState() {
  if (busy.value || checking.value || creating.value || props.active === false) return
  if (!selected.value) {
    checking.value = true
    try {
      await load(false, true)
    } finally {
      checking.value = false
    }
    return
  }
  // Poll only the version. Never replace an open form or silently rebase its edits.
  checking.value = true
  const id = selected.value.id
  try {
    const current = await request<Order>(`/work-orders/${id}`)
    if (selected.value?.id === id) changed.value = current.version !== selected.value.version
  } catch (e) {
    error.value = e instanceof Error ? e.message : '工单状态核对失败。'
    if (selected.value?.id === id && e instanceof ApiError && [401, 403, 404].includes(e.status))
      selected.value = null
  } finally {
    checking.value = false
  }
}
const unsubscribe = subscribePlantEvents((event) => {
  if (!event || (event.plantId === props.plantId && event.eventType === 'WorkOrderChanged'))
    void refreshState()
})
const timer = setInterval(() => {
  void refreshState()
}, 5000)
onUnmounted(() => {
  unsubscribe()
  clearInterval(timer)
})
</script>
<template>
  <section :class="detailId ? 'work-order-container' : 'asset-detail'">
    <div v-if="!detailId" class="section-title">
      <div>
        <p class="eyebrow">MAINTENANCE</p>
        <h2>{{ history ? '维修历史' : '维护工单' }}</h2>
      </div>
      <div class="actions">
        <el-button :disabled="busy" @click="load()">刷新工单</el-button
        ><el-button
          v-if="writer && !archived && !history"
          :disabled="busy"
          @click="openOrder('new')"
          type="primary"
        >
          创建工单
        </el-button>
      </div>
    </div>
    <el-alert
      v-if="!detailId && error && !selected && !creating"
      :title="error"
      type="error"
      :closable="false"
      show-icon
    />
    <el-alert
      v-if="notice && !detailId"
      :title="notice"
      type="success"
      :closable="false"
      show-icon
    />
    <div v-if="!detailId">
      <el-table
        :data="orders"
        v-loading="busy"
        row-key="id"
        empty-text="暂无工单"
        @row-click="(order: Order) => !busy && openOrder(order.id)"
      >
        <el-table-column label="工单" min-width="220"
          ><template #default="{ row }"
            ><el-button link type="primary" @click.stop="openOrder(row.id)">{{
              row.title
            }}</el-button
            ><br /><small>{{ row.number }}</small></template
          ></el-table-column
        >
        <el-table-column label="状态" min-width="150"
          ><template #default="{ row }"
            ><el-tag>{{ labels[row.status] }}</el-tag></template
          ></el-table-column
        >
        <el-table-column prop="priority" label="优先级" width="100" />
        <el-table-column prop="version" label="版本" width="80" />
      </el-table>
      <el-button v-if="cursor" :disabled="busy" @click="load(true)">加载更多</el-button>
    </div>
    <div v-if="detailId" class="work-order-page">
      <div class="section-title">
        <div>
          <p class="eyebrow">MAINTENANCE / {{ assetLabel ?? '维护工单' }}</p>
          <h2>
            {{
              creating
                ? '创建维护工单'
                : selected
                  ? selected.number + ' · ' + selected.title
                  : '工单详情'
            }}
          </h2>
        </div>
      </div>
      <el-skeleton v-if="busy && !selected && !creating" :rows="6" animated />
      <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
      <el-alert v-if="notice" :title="notice" type="success" :closable="false" show-icon />
      <div v-if="changed" class="error-banner" role="status">
        工单已被其他操作更新，当前内容保留为读取时的快照。请重新核对后操作。
        <p v-if="dirty">请先复制保留未保存内容，或关闭面板放弃编辑后重新打开。</p>
        <el-button :disabled="busy || dirty" @click="select(selected!)" type="primary"
          >读取最新工单</el-button
        >
      </div>
      <el-steps
        :active="selected?.status === 'Closed' ? 3 : Math.max(0, stepIndex)"
        finish-status="success"
        align-center
        aria-label="工单处理流程"
      >
        <el-step v-for="step in steps" :key="step" :title="step" />
      </el-steps>
      <p class="workflow-hint">
        {{
          creating
            ? '填写任务后创建并打开，即可安排维护。暂未准备好的任务可先保存草稿。'
            : selected
              ? nextHints[selected.status]
              : '正在读取工单；失败后可返回设备重试。'
        }}
      </p>
      <p v-if="selected?.status === 'Cancelled'" class="quality-tag untrusted">已取消 · 流程终止</p>
      <el-form
        ref="createForm"
        :model="createModel"
        label-position="top"
        v-if="creating"
        class="editor-panel"
        @submit.prevent="submitCreate"
      >
        <el-form-item
          label="标题"
          prop="title"
          :rules="[
            { required: true, whitespace: true, message: '请填写工单标题', trigger: 'blur' },
          ]"
          ><el-input v-model="title" required maxlength="200" /></el-form-item
        ><el-form-item label="任务说明"
          ><el-input
            type="textarea"
            :rows="4"
            v-model="description"
            maxlength="8000" /></el-form-item
        ><el-form-item label="优先级"
          ><el-select filterable v-model="priority">
            <el-option value="Low" label="Low" />
            <el-option value="Medium" label="Medium" />
            <el-option value="High" label="High" /> </el-select></el-form-item
        ><el-form-item label="责任人"
          ><el-select filterable v-model="assigned" placeholder="未指派">
            <el-option value="" label="未指派" />
            <el-option
              v-for="u in users"
              :key="u.id"
              :value="u.id"
              :label="u.displayName"
            /> </el-select></el-form-item
        ><el-form-item label="关联告警"
          ><el-select filterable v-model="alarmId" placeholder="不关联">
            <el-option value="" label="不关联" />
            <el-option
              v-for="a in alarms"
              :key="a.id"
              :value="a.id"
              :label="
                a.ruleName + ' · ' + a.state + ' · ' + new Date(a.startedAt).toLocaleString('zh-CN')
              "
            /> </el-select
        ></el-form-item>
        <div class="actions workflow-footer">
          <el-button native-type="submit" :disabled="busy" type="primary">创建并打开</el-button>
          <el-button native-type="submit" value="draft" :disabled="busy">仅保存草稿</el-button>
        </div>
      </el-form>
      <section v-if="selected" class="editor-panel">
        <h3>{{ selected.title }} · {{ labels[selected.status] }}</h3>
        <p class="work-order-text">{{ selected.description }}</p>
        <p>
          优先级 {{ selected.priority }} · 责任人 {{ selected.assignedToUserId || '未指派' }} · v{{
            selected.version
          }}
        </p>
        <p v-if="selected.alarmId">关联告警 {{ selected.alarmId }}（工单关闭不改变告警状态）</p>
        <p v-if="selected.resolution" class="work-order-text">
          处理结果：{{ selected.resolution }}
        </p>
        <p v-if="selected.closedAt">
          关闭于 {{ new Date(selected.closedAt).toLocaleString('zh-CN') }}
        </p>
        <div v-if="writer && primaryAction">
          <el-form
            label-position="top"
            @submit.prevent="mutate(primaryAction!.status, selected!.status === 'InProgress')"
          >
            <el-form-item v-if="selected.status === 'InProgress'" label="处理结果（必填）"
              ><el-input
                type="textarea"
                :rows="4"
                v-model="resolution"
                required
                maxlength="8000"
                placeholder="记录已执行的检查、处理措施与验证结果"
              />
            </el-form-item>
            <div class="actions workflow-actions workflow-footer">
              <el-button
                native-type="submit"
                :disabled="
                  busy ||
                  changed ||
                  !!note.trim() ||
                  (selected.status === 'InProgress' && !resolution.trim())
                "
                type="primary"
              >
                {{ primaryAction.label }}
              </el-button>
              <el-button
                v-if="selected.status === 'InProgress'"
                native-type="button"
                :disabled="busy || changed || !!note.trim() || !resolution.trim()"
                @click="mutate('Completed')"
              >
                仅提交完成，留待复核
              </el-button>
            </div>
          </el-form>
          <details class="workflow-secondary">
            <summary>维护备注（可选）</summary>
            <el-form label-position="top" @submit.prevent="mutate()">
              <el-form-item label="记录维护过程"
                ><el-input type="textarea" :rows="4" v-model="note" required maxlength="8000"
              /></el-form-item>
              <p v-if="note.trim()" class="path-note">请先保存备注，再提交下一步。</p>
              <el-button :disabled="busy || changed" native-type="submit" type="primary"
                >保存备注</el-button
              >
            </el-form>
          </details>
          <details v-if="canCancel || selected.status === 'Completed'" class="workflow-secondary">
            <summary>更多操作 · {{ canCancel ? '取消任务' : '退回返工' }}</summary>
            <el-form-item :label="canCancel ? '取消原因（必填）' : '返工原因（必填）'"
              ><el-input type="textarea" :rows="4" v-model="reason" maxlength="8000" />
            </el-form-item>
            <el-button
              :disabled="busy || changed || !!note.trim() || !reason.trim()"
              @click="mutate(canCancel ? 'Cancelled' : 'InProgress')"
            >
              {{ canCancel ? '取消工单' : '退回返工' }}
            </el-button>
          </details>
        </div>
        <h3>处理记录</h3>
        <p v-if="!notes.length">暂无备注。</p>
        <article v-for="n in notes" :key="n.id">
          <small
            >{{ n.kind === 'AIInvestigation' ? '调查备注（已审批）' : n.kind }} ·
            {{ new Date(n.occurredAt).toLocaleString('zh-CN') }} · {{ n.actorUserId }}</small
          >
          <p class="work-order-text">{{ n.text }}</p>
        </article>
      </section>
    </div>
  </section>
</template>

<style scoped>
.work-order-text {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
