<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ApiError, listAll, request, type DirectoryRecord } from '../api'
import { useSession } from '../stores/session'
import DirectoryEditor from '../components/DirectoryEditor.vue'
import MeasurementPanel from '../components/MeasurementPanel.vue'
const session = useSession()
const plants = ref<DirectoryRecord[]>([])
const locations = ref<DirectoryRecord[]>([])
const assets = ref<DirectoryRecord[]>([])
const plantId = ref('')
const locationId = ref('')
const asset = ref<DirectoryRecord | null>(null)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const notice = ref('')
const expired = ref(false)
const editor = ref<{ kind: string; record: DirectoryRecord | null } | null>(null)
const plant = computed(() => plants.value.find(x => x.id === plantId.value))
const location = computed(() => locations.value.find(x => x.id === locationId.value))
const visibleAssets = computed(() => assets.value.filter(x => !locationId.value || x.locationId === locationId.value))
const tree = computed(() => {
  const rows: { item: DirectoryRecord; depth: number }[] = []
  function walk(parent: string | null, depth: number) {
    locations.value.filter(x => (x.parentLocationId ?? null) === parent).sort((a, b) => Number(a.sortOrder ?? 0) - Number(b.sortOrder ?? 0) || a.code.localeCompare(b.code)).forEach(item => { rows.push({ item, depth }); walk(item.id, depth + 1) })
  }
  walk(null, 0); return rows
})
function fail(e: unknown) {
  error.value = e instanceof Error ? e.message : '操作失败。'
  expired.value = e instanceof ApiError && e.status === 401
}
async function loadPlant() {
  loading.value = true; error.value = ''; asset.value = null; locations.value = []; assets.value = []; editor.value = null
  try {
    if (plantId.value) [locations.value, assets.value] = await Promise.all([
      listAll(`/plants/${plantId.value}/locations`), listAll(`/assets?plantId=${plantId.value}`)])
    if (!locations.value.some(x => x.id === locationId.value)) locationId.value = ''
  } catch (e) { fail(e) }
  finally { loading.value = false }
}
async function refresh() {
  loading.value = true; error.value = ''; notice.value = ''; plants.value = []; locations.value = []; assets.value = []; asset.value = null; editor.value = null
  try {
    await session.load()
    if (!session.user) throw new ApiError(401, '会话已失效，请重新登录。')
    plants.value = await listAll('/plants')
    if (!plants.value.some(x => x.id === plantId.value)) plantId.value = plants.value.find(x => !x.isArchived)?.id ?? plants.value[0]?.id ?? ''
    await loadPlant()
  } catch (e) { fail(e) }
  finally { loading.value = false }
}
async function selectAsset(id: string) {
  loading.value = true; error.value = ''; editor.value = null; asset.value = null
  try { asset.value = await request<DirectoryRecord>(`/assets/${id}`) }
  catch (e) { fail(e) }
  finally { loading.value = false }
}
function edit(kind: string, record: DirectoryRecord | null) { error.value = ''; notice.value = ''; editor.value = { kind, record } }
async function save(input: object) {
  if (!editor.value) return
  saving.value = true; error.value = ''; notice.value = ''
  const { kind, record } = editor.value
  try {
    const path = record ? `/${kind}/${record.id}` : kind === 'plants' ? '/plants' : `/plants/${plantId.value}/${kind}`
    const saved = await request<DirectoryRecord>(path, record ? 'PATCH' : 'POST', input)
    if (kind === 'plants') plantId.value = saved.id
    await refresh()
    if (kind === 'assets') await selectAsset(saved.id)
    if (kind === 'locations') locationId.value = saved.id
    notice.value = `已保存 ${saved.name} · 版本 ${saved.version} · 已记录审计`
  } catch (e) { fail(e) }
  finally { saving.value = false }
}
onMounted(refresh)
</script>
<template>
  <header class="topbar"><span>工作空间 / 资产目录</span><span class="stage-chip">{{ session.user?.roles.join(' / ') }}</span></header>
  <section class="page-heading"><div><p class="eyebrow">ASSET DIRECTORY / 01</p><h1>了解工厂中的每一台设备。</h1><p class="intro">{{ session.admin ? '管理授权工厂内的资产。每一次保存都检查版本并记录审计。' : '浏览授权工厂的设备目录。当前角色可读取资产信息。' }}</p></div><button :disabled="loading || saving" @click="refresh">刷新目录</button></section>
  <p v-if="error" class="error-banner" role="alert">{{ error }} <RouterLink v-if="expired" to="/login">重新登录</RouterLink></p>
  <p v-if="notice" class="success-banner" role="status">{{ notice }}</p>
  <div class="directory-toolbar"><label>工厂<select v-model="plantId" :disabled="loading || saving" @change="locationId = ''; loadPlant()"><option v-if="!plants.length" value="">无授权工厂</option><option v-for="p in plants" :key="p.id" :value="p.id">{{ p.name }}{{ p.isArchived ? '（已归档）' : '' }}</option></select></label><div v-if="session.admin" class="actions"><button class="secondary" :disabled="loading || saving" @click="edit('plants', null)">新增工厂</button><button class="secondary" :disabled="!plant || loading || saving" @click="edit('plants', plant ?? null)">编辑工厂</button></div></div>
  <section v-if="plant" class="directory-grid" :aria-busy="loading">
    <aside class="location-panel"><div class="section-title"><h2>位置树</h2><button v-if="session.admin && !plant.isArchived" class="small-button" :disabled="loading || saving" @click="edit('locations', null)">新增位置</button></div><button class="tree-item" :class="{ selected: !locationId }" :disabled="loading || saving" @click="locationId = ''; asset = null; editor = null">全部位置 <span>{{ assets.length }}</span></button><button v-for="row in tree" :key="row.item.id" class="tree-item" :class="{ selected: locationId === row.item.id }" :style="{ paddingLeft: `${14 + row.depth * 16}px` }" :disabled="loading || saving" @click="locationId = row.item.id; asset = null; editor = null">{{ row.depth ? '↳ ' : '↗ ' }}{{ row.item.name }}<small v-if="row.item.isArchived">已归档</small></button><p v-if="!locations.length" class="path-note">{{ loading ? '正在加载位置…' : '暂无位置。' }}</p><button v-if="session.admin && location && !plant.isArchived" class="secondary" :disabled="loading || saving" @click="edit('locations', location ?? null)">编辑所选位置</button></aside>
    <div class="asset-list-panel"><div class="section-title"><div><p class="eyebrow">{{ plant.code }} / {{ location?.code ?? 'ALL LOCATIONS' }}</p><h2>{{ location?.name ?? '全部设备' }}</h2></div><button v-if="session.admin && !plant.isArchived" class="small-button" :disabled="loading || saving" @click="edit('assets', null)">新增设备</button></div><div class="asset-table"><div class="table-heading"><span>设备</span><span>生命周期 / 版本</span></div><button v-for="item in visibleAssets" :key="item.id" class="asset-row" :class="{ selected: asset?.id === item.id }" :disabled="loading || saving" @click="selectAsset(item.id)"><span><b>{{ item.code }}</b><strong>{{ item.name }}</strong><small>{{ locations.find(x => x.id === item.locationId)?.name }}</small></span><span class="asset-state">{{ item.isArchived ? '已归档' : item.lifecycleStatus }}<small>v{{ item.version }}</small></span></button><p v-if="!visibleAssets.length" class="path-note">{{ loading ? '正在加载设备…' : '此位置暂无设备。' }}</p></div></div>
  </section>
  <DirectoryEditor v-if="editor" :key="`${editor.kind}-${editor.record?.id ?? 'new'}`" :kind="editor.kind" :record="editor.record" :locations="locations" :busy="saving" @save="save" @cancel="editor = null" />
  <section v-else-if="asset" class="asset-detail"><div class="section-title"><div><p class="eyebrow">{{ plant?.name }} → {{ locations.find(x => x.id === asset?.locationId)?.name }} → {{ asset.code }}</p><h2>{{ asset.name }}</h2></div><button v-if="session.admin && !plant?.isArchived" :disabled="loading || saving" @click="edit('assets', asset)">编辑设备</button></div><dl class="detail-grid"><div><dt>设备类型</dt><dd>{{ asset.assetType }}</dd></div><div><dt>生命周期</dt><dd>{{ asset.lifecycleStatus }}</dd></div><div><dt>重要程度</dt><dd>{{ asset.criticality }}</dd></div><div><dt>版本</dt><dd>{{ asset.version }}</dd></div><div><dt>制造商 / 型号</dt><dd>{{ asset.manufacturer || '未填写' }} / {{ asset.model || '未填写' }}</dd></div><div><dt>序列号</dt><dd>{{ asset.serialNumber || '未填写' }}</dd></div><div><dt>安装时间</dt><dd>{{ asset.installedAt ? new Date(asset.installedAt).toLocaleString('zh-CN') : '未填写' }}</dd></div><div><dt>归档</dt><dd>{{ asset.isArchived ? '已归档，历史保留' : '未归档' }}</dd></div></dl><p class="path-note"><RouterLink :to="{ path: '/alarms', query: { plantId: asset.plantId, assetId: asset.id } }">查看此设备的告警 →</RouterLink></p><MeasurementPanel :key="asset.id" :asset-id="asset.id" :plant-id="asset.plantId" :admin="session.admin" :archived="asset.isArchived || !!plant?.isArchived" /></section>
  <p v-if="!plant && !loading && !error" class="path-note">当前账号没有工厂授权。</p>
</template>


