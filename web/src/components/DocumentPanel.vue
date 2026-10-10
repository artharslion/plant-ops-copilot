<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { downloadDocument, request } from '../api'
import { useSession } from '../stores/session'
import { subscribePlantEvents } from '../realtime'
const props = defineProps<{ plantId: string; assetId?: string; archived?: boolean }>()
type Doc = {
  id: string
  plantId: string
  assetId: string | null
  title: string
  category: string
  visibility: string
  allowedRoles: string[]
  currentPublishedVersionId: string | null
  version: number
  isArchived: boolean
}
type Version = {
  id: string
  versionNumber: number
  fileName: string
  mediaType: string
  sha256: string
  sizeBytes: number
  status: string
  uploadedAt: string
  indexStatus: string
}
const session = useSession(),
  writer = computed(() =>
    session.user?.roles.some((x) => ['Admin', 'MaintenanceEngineer'].includes(x)),
  )
const rows = ref<Doc[]>([]),
  selected = ref<Doc | null>(null),
  versions = ref<Version[]>([]),
  cursor = ref<string | null>(null)
const busy = ref(false),
  error = ref(''),
  notice = ref(''),
  editing = ref(false),
  creating = ref(false)
const title = ref(''),
  category = ref('Maintenance'),
  visibility = ref('PlantReaders'),
  allowedRoles = ref<string[]>(['Admin', 'MaintenanceEngineer']),
  file = ref<File | null>(null)
async function load(more = false) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    const page = await request<{ items: Doc[]; nextCursor: string | null }>(
      `/documents?plantId=${props.plantId}${props.assetId ? `&assetId=${props.assetId}` : ''}&limit=50${more && cursor.value ? `&cursor=${cursor.value}` : ''}`,
    )
    rows.value = more ? [...rows.value, ...page.items] : page.items
    cursor.value = page.nextCursor
    if (selected.value) {
      const current = rows.value.find((x) => x.id === selected.value?.id)
      selected.value = current ?? null
      versions.value = current ? await request(`/documents/${current.id}/versions`) : []
    }
  } catch (e) {
    rows.value = []
    selected.value = null
    versions.value = []
    error.value = e instanceof Error ? e.message : '读取失败。'
  } finally {
    busy.value = false
  }
}
async function select(d: Doc) {
  busy.value = true
  error.value = ''
  editing.value = false
  creating.value = false
  selected.value = null
  try {
    selected.value = await request(`/documents/${d.id}`)
    versions.value = await request(`/documents/${d.id}/versions`)
    title.value = d.title
    category.value = d.category
    visibility.value = d.visibility
    allowedRoles.value = [...d.allowedRoles]
    file.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : '读取失败。'
  } finally {
    busy.value = false
  }
}
function prepare() {
  selected.value = null
  creating.value = true
  editing.value = false
  title.value = ''
  category.value = 'Maintenance'
  visibility.value = 'PlantReaders'
  allowedRoles.value = ['Admin', 'MaintenanceEngineer']
  file.value = null
  error.value = ''
  notice.value = ''
}
function metadata() {
  return {
    plantId: props.plantId,
    assetId: selected.value?.assetId ?? props.assetId ?? null,
    title: title.value,
    category: category.value,
    visibility: visibility.value,
    allowedRoles: allowedRoles.value,
    version: selected.value?.version ?? 0,
  }
}
async function upload() {
  if (!file.value) {
    error.value = '请选择文件。'
    return
  }
  busy.value = true
  error.value = ''
  notice.value = ''
  try {
    const data = new FormData()
    data.set('metadata', JSON.stringify(metadata()))
    const extension = file.value.name.split('.').pop()?.toLowerCase() ?? ''
    data.set(
      'file',
      file.value.type
        ? file.value
        : new Blob([file.value], {
            type:
              (
                { md: 'text/markdown', txt: 'text/plain', pdf: 'application/pdf' } as Record<
                  string,
                  string
                >
              )[extension] ?? 'application/octet-stream',
          }),
      file.value.name,
    )
    const d = await request<Doc>(
      selected.value ? `/documents/${selected.value.id}/versions` : '/documents',
      'POST',
      data,
    )
    creating.value = false
    editing.value = false
    await select(d)
    notice.value = '已上传草稿版本；发布后可授权下载。'
  } catch (e) {
    error.value = e instanceof Error ? e.message : '上传失败。'
  } finally {
    busy.value = false
  }
}
async function action(kind: string, number?: number) {
  if (!selected.value) return
  busy.value = true
  error.value = ''
  notice.value = ''
  try {
    const d = await request<Doc>(
      `/documents/${selected.value.id}${kind === 'configure' || kind === 'archive' ? '' : '/' + kind}`,
      kind === 'configure' || kind === 'archive' ? 'PATCH' : 'POST',
      kind === 'configure' || kind === 'archive'
        ? { ...metadata(), isArchived: kind === 'archive' }
        : { version: selected.value.version, versionNumber: number },
    )
    editing.value = false
    if (
      d.isArchived ||
      (d.visibility === 'RestrictedRoles' &&
        !d.allowedRoles.some((r) => session.user?.roles.includes(r)))
    ) {
      selected.value = null
      versions.value = []
      busy.value = false
      await load()
    } else await select(d)
    notice.value = '文档变更已保存，授权与发布状态立即生效。'
  } catch (e) {
    error.value = e instanceof Error ? e.message : '保存失败。'
  } finally {
    busy.value = false
  }
}
async function download(v: Version) {
  if (!selected.value) return
  busy.value = true
  error.value = ''
  try {
    await downloadDocument(
      `/documents/${selected.value.id}/versions/${v.versionNumber}/download`,
      v.fileName,
    )
    notice.value = '已通过授权检查并下载。'
  } catch (e) {
    error.value = e instanceof Error ? e.message : '下载失败。'
  } finally {
    busy.value = false
  }
}
onMounted(() => load())
const unsubscribe = subscribePlantEvents((event) => {
  if (
    !busy.value &&
    !creating.value &&
    !editing.value &&
    !file.value &&
    (!event || (event.plantId === props.plantId && event.eventType === 'DocumentsChanged'))
  )
    void load()
})
const timer = setInterval(() => {
  if (!creating.value && !editing.value && !file.value) void load()
}, 5000)
onUnmounted(() => {
  unsubscribe()
  clearInterval(timer)
})
</script>
<template>
  <section class="asset-detail">
    <div class="section-title">
      <div>
        <p class="eyebrow">KNOWLEDGE / DOCUMENTS</p>
        <h2>设备与工厂资料</h2>
      </div>
      <div class="actions">
        <button class="secondary" :disabled="busy" @click="load()">刷新资料</button
        ><button v-if="writer && !archived" :disabled="busy" @click="prepare">上传新资料</button>
      </div>
    </div>
    <p v-if="error" class="error-banner" role="alert">{{ error }}</p>
    <p v-if="notice" class="success-banner" role="status">{{ notice }}</p>
    <button v-for="d in rows" :key="d.id" class="asset-row" :disabled="busy" @click="select(d)">
      <span
        ><strong>{{ d.title }}</strong
        ><small>{{ d.category }} · {{ d.assetId ? '设备资料' : '工厂资料' }}</small></span
      ><span
        >{{ d.currentPublishedVersionId ? '已发布' : '未发布' }} · v{{ d.version
        }}<small>{{
          d.visibility === 'PlantReaders' ? '工厂读者' : d.allowedRoles.join(' / ')
        }}</small></span
      >
    </button>
    <p v-if="!rows.length" class="path-note">{{ busy ? '读取中…' : '暂无可访问资料。' }}</p>
    <button v-if="cursor" :disabled="busy" @click="load(true)">加载更多资料</button>
    <form
      v-if="creating || (selected && writer && editing)"
      class="editor-panel"
      @submit.prevent="creating ? upload() : action('configure')"
    >
      <h3>{{ creating ? '上传资料' : '资料信息与读取权限' }}</h3>
      <label>资料标题<input v-model="title" required maxlength="200" /></label
      ><label>分类<input v-model="category" required maxlength="64" /></label
      ><label
        >读取范围<select v-model="visibility">
          <option value="PlantReaders">工厂读者</option>
          <option value="RestrictedRoles">指定角色</option>
        </select></label
      >
      <div v-if="visibility === 'RestrictedRoles'">
        <label
          v-for="r in ['Viewer', 'Operator', 'MaintenanceEngineer', 'Admin']"
          :key="r"
          class="check-label"
          ><input v-model="allowedRoles" type="checkbox" :value="r" />{{ r }}</label
        >
      </div>
      <label v-if="creating"
        >文件（PDF / Markdown / UTF-8 TXT，最多 10 MiB）<input
          type="file"
          accept=".pdf,.md,.txt"
          @change="file = ($event.target as HTMLInputElement).files?.[0] ?? null" /></label
      ><button :disabled="busy">{{ creating ? '上传草稿' : '保存资料信息' }}</button>
    </form>
    <section v-if="selected" class="editor-panel">
      <div class="section-title">
        <h3>{{ selected.title }}</h3>
        <div v-if="writer" class="actions">
          <button class="secondary" :disabled="busy" @click="editing = !editing">
            编辑资料信息</button
          ><button class="secondary" :disabled="busy" @click="action('archive')">归档资料</button>
        </div>
      </div>
      <p>
        {{
          selected.visibility === 'PlantReaders'
            ? '授权工厂读者可读'
            : '限定角色：' + selected.allowedRoles.join(' / ')
        }}
        · 元数据 v{{ selected.version }}
      </p>
      <form v-if="writer" @submit.prevent="upload">
        <label
          >上传下一版本<input
            type="file"
            accept=".pdf,.md,.txt"
            @change="file = ($event.target as HTMLInputElement).files?.[0] ?? null" /></label
        ><button :disabled="busy">保存新版本草稿</button>
      </form>
      <article v-for="v in versions" :key="v.id" class="document-version">
        <h4>版本 {{ v.versionNumber }} · {{ v.status }}</h4>
        <p>
          {{ v.fileName }} · {{ (v.sizeBytes / 1024).toFixed(1) }} KiB ·
          {{ new Date(v.uploadedAt).toLocaleString('zh-CN') }}
        </p>
        <p class="path-note">
          {{
            v.indexStatus === 'NeedsText'
              ? 'PDF 尚无提取文本；可下载，尚不能用于证据查询。'
              : v.indexStatus === 'Ready'
                ? '文本片段已就绪；当前发布版本可供授权证据查询。'
                : v.indexStatus === 'NotRequested'
                  ? '旧资料将在授权查询时生成文本片段。'
                  : '资料为空、超出解析上限或暂不可解析，未纳入证据查询。'
          }}<br />SHA-256 {{ v.sha256 }}
        </p>
        <div class="actions">
          <button
            v-if="v.status === 'Published' && selected.currentPublishedVersionId"
            :disabled="busy"
            @click="download(v)"
          >
            下载版本 {{ v.versionNumber }}</button
          ><button
            v-if="writer && v.status === 'Draft'"
            :disabled="busy"
            @click="action('publish', v.versionNumber)"
          >
            发布版本 {{ v.versionNumber }}</button
          ><button
            v-if="writer && v.status === 'Published'"
            class="secondary"
            :disabled="busy"
            @click="action('withdraw', v.versionNumber)"
          >
            撤回版本 {{ v.versionNumber }}
          </button>
        </div>
      </article>
    </section>
  </section>
</template>
