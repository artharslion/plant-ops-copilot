<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { request } from '../api'
import { useSession } from '../stores/session'
import { subscribePlantEvents } from '../realtime'
const props = defineProps<{ assetId: string; plantId: string; archived?: boolean; history?: boolean }>()
type Order = { id: string; number: string; title: string; description: string; priority: string; status: string; version: number; alarmId: string | null; assignedToUserId: string | null; resolution: string | null; createdAt: string; completedAt: string | null; closedAt: string | null }
type Note = { id: string; text: string; kind: string; actorUserId: string; occurredAt: string }
const session = useSession()
const writer = computed(() => session.user?.roles.some(x => ['Admin', 'MaintenanceEngineer'].includes(x)))
const orders = ref<Order[]>([]), selected = ref<Order | null>(null), notes = ref<Note[]>([])
const users = ref<{ id: string; displayName: string }[]>([]), alarms = ref<{ id: string; state: string; ruleName: string; startedAt: string }[]>([])
const cursor = ref<string | null>(null), busy = ref(false), error = ref(''), notice = ref(''), creating = ref(false)
const title = ref(''), description = ref(''), priority = ref('Medium'), assigned = ref(''), alarmId = ref(''), reason = ref(''), resolution = ref(''), note = ref('')
const labels: Record<string, string> = { Draft: '草稿', Open: '待执行', InProgress: '执行中', Completed: '已完成', Closed: '已关闭', Cancelled: '已取消' }
const transitions = computed(() => ({ Draft: ['Open', 'Cancelled'], Open: ['InProgress', 'Cancelled'], InProgress: ['Completed', 'Cancelled'], Completed: ['Closed', 'InProgress'] }[selected.value?.status ?? ''] ?? []))
async function load(more = false) {
  busy.value = true; error.value = ''; selected.value = null; notes.value = []; creating.value = false
  try { const page = await request<{ items: Order[]; nextCursor: string | null }>(`/work-orders?assetId=${props.assetId}&history=${!!props.history}&limit=50${more && cursor.value ? `&cursor=${encodeURIComponent(cursor.value)}` : ''}`); orders.value = more ? [...orders.value, ...page.items] : page.items; cursor.value = page.nextCursor }
  catch (e) { orders.value = []; error.value = e instanceof Error ? e.message : '加载失败。' }
  finally { busy.value = false }
}
async function select(w: Order) { busy.value = true; error.value = ''; selected.value = null; notes.value = []; creating.value = false
  try { selected.value = await request<Order>(`/work-orders/${w.id}`); notes.value = await request<Note[]>(`/work-orders/${w.id}/notes`); reason.value = ''; resolution.value = selected.value.resolution ?? ''; note.value = '' }
  catch (e) { error.value = e instanceof Error ? e.message : '加载失败。' } finally { busy.value = false }
}
async function prepare() { busy.value = true; error.value = ''; selected.value = null
  try { users.value = await request(`/plants/${props.plantId}/maintenance-users`); alarms.value = (await request<{ items: typeof alarms.value }>(`/alarms?plantId=${props.plantId}&assetId=${props.assetId}&limit=200`)).items; creating.value = true }
  catch (e) { error.value = e instanceof Error ? e.message : '加载失败。' } finally { busy.value = false }
}
async function create() { busy.value = true; error.value = ''; notice.value = ''
  try { const w = await request<Order>('/work-orders', 'POST', { assetId: props.assetId, title: title.value, description: description.value, priority: priority.value, assignedToUserId: assigned.value || null, alarmId: alarmId.value || null }); title.value = ''; description.value = ''; await load(); await select(w); notice.value = '工单已创建，审计已记录。' }
  catch (e) { error.value = e instanceof Error ? e.message : '创建失败。' } finally { busy.value = false }
}
async function mutate(status?: string) { if (!selected.value) return; busy.value = true; error.value = ''; notice.value = ''
  try { const w = await request<Order>(`/work-orders/${selected.value.id}/${status ? 'transition' : 'notes'}`, 'POST', status ? { version: selected.value.version, status, reason: reason.value || null, resolution: resolution.value || null } : { version: selected.value.version, text: note.value }); await load(); await select(w); notice.value = '维护记录已保存。' }
  catch (e) { error.value = e instanceof Error ? e.message : '保存失败。' } finally { busy.value = false }
}
onMounted(() => load())
async function refreshState() {
  if (busy.value || creating.value) return
  if (selected.value && (note.value || reason.value || resolution.value !== (selected.value.resolution ?? ''))) { notice.value = '维护状态可能已变化。请刷新工单并检查后再保存。'; return }
  if (selected.value) await select(selected.value); else await load()
}
const unsubscribe = subscribePlantEvents(event => { if (!event || event.plantId === props.plantId && event.eventType === 'WorkOrderChanged') void refreshState() })
const timer = setInterval(() => { void refreshState() }, 5000)
onUnmounted(() => { unsubscribe(); clearInterval(timer) })
</script>
<template>
  <section class="asset-detail"><div class="section-title"><div><p class="eyebrow">MAINTENANCE</p><h2>{{ history ? '维修历史' : '维护工单' }}</h2></div><div class="actions"><button :disabled="busy" class="secondary" @click="load()">刷新工单</button><button v-if="writer && !archived && !history" :disabled="busy" @click="prepare">创建工单</button></div></div>
    <p v-if="error" class="error-banner" role="alert">{{ error }}</p><p v-if="notice" class="success-banner" role="status">{{ notice }}</p>
    <div class="asset-table"><button v-for="w in orders" :key="w.id" class="asset-row" :disabled="busy" @click="select(w)"><span><strong>{{ w.title }}</strong><small>{{ w.number }} · {{ new Date(w.createdAt).toLocaleString('zh-CN') }}</small></span><span>{{ labels[w.status] }} · v{{ w.version }}</span></button><p v-if="!orders.length" class="path-note">{{ busy ? '正在加载…' : '暂无工单。' }}</p><button v-if="cursor" :disabled="busy" @click="load(true)">加载更多</button></div>
    <form v-if="creating" class="editor-panel" @submit.prevent="create"><h3>创建维护工单</h3><label>标题<input v-model="title" required maxlength="200" /></label><label>任务说明<textarea v-model="description" maxlength="8000" /></label><label>优先级<select v-model="priority"><option>Low</option><option>Medium</option><option>High</option></select></label><label>责任人<select v-model="assigned"><option value="">未指派</option><option v-for="u in users" :key="u.id" :value="u.id">{{ u.displayName }}</option></select></label><label>关联告警<select v-model="alarmId"><option value="">不关联</option><option v-for="a in alarms" :key="a.id" :value="a.id">{{ a.ruleName }} · {{ a.state }} · {{ new Date(a.startedAt).toLocaleString('zh-CN') }}</option></select></label><button :disabled="busy">保存草稿</button></form>
    <section v-if="selected" class="editor-panel"><h3>{{ selected.title }} · {{ labels[selected.status] }}</h3><p>{{ selected.description }}</p><p>优先级 {{ selected.priority }} · 责任人 {{ selected.assignedToUserId || '未指派' }} · v{{ selected.version }}</p><p v-if="selected.alarmId">关联告警 {{ selected.alarmId }}（工单关闭不改变告警状态）</p><p v-if="selected.resolution">处理结果：{{ selected.resolution }}</p><p v-if="selected.closedAt">关闭于 {{ new Date(selected.closedAt).toLocaleString('zh-CN') }}</p>
      <div v-if="writer && transitions.length"><label>处理结果（完成必填）<textarea v-model="resolution" maxlength="8000" /></label><label>原因（取消或返工必填）<textarea v-model="reason" maxlength="8000" /></label><div class="actions"><button v-for="to in transitions" :key="to" :disabled="busy" @click="mutate(to)">{{ to === 'InProgress' && selected.status === 'Completed' ? '返工' : labels[to] }}</button></div><form @submit.prevent="mutate()"><label>维护备注<textarea v-model="note" required maxlength="8000" /></label><button :disabled="busy">追加备注</button></form></div>
      <h3>处理记录</h3><p v-if="!notes.length">暂无备注。</p><article v-for="n in notes" :key="n.id"><small>{{ n.kind }} · {{ new Date(n.occurredAt).toLocaleString('zh-CN') }} · {{ n.actorUserId }}</small><p>{{ n.text }}</p></article>
    </section>
  </section>
</template>
