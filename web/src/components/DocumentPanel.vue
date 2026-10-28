<script setup lang="ts">
import { messageRef, t } from '../i18n'
import { formatDateTime as at } from '../time'
import { computed, onMounted, onUnmounted, ref } from 'vue'
import SidePanel from './SidePanel.vue'
import type { FormInstance } from 'element-plus'
import { ApiError, downloadDocument, request } from '../api'
import { useSession } from '../stores/session'
import { subscribePlantEvents } from '../realtime'
const props = withDefaults(
  defineProps<{
    plantId: string
    assetId?: string
    archived?: boolean
    active?: boolean
  }>(),
  { active: true },
)
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
  error = messageRef(),
  notice = messageRef(),
  editing = ref(false),
  creating = ref(false)
const title = ref(''),
  category = ref('Maintenance'),
  visibility = ref('PlantReaders'),
  allowedRoles = ref<string[]>(['Admin', 'MaintenanceEngineer']),
  file = ref<File | null>(null)
async function load(more = false, background = false) {
  if (busy.value) return
  if (!background) busy.value = true
  error.value = ''
  try {
    const page = await request<{ items: Doc[]; nextCursor: string | null }>(
      `/documents?plantId=${props.plantId}${props.assetId ? `&assetId=${props.assetId}` : ''}&limit=50${more && cursor.value ? `&cursor=${cursor.value}` : ''}`,
    )
    rows.value = more ? [...rows.value, ...page.items] : page.items
    cursor.value = page.nextCursor
    if (selected.value && !background) {
      const current = rows.value.find((x) => x.id === selected.value?.id)
      selected.value = current ?? null
      versions.value = current ? await request(`/documents/${current.id}/versions`) : []
    }
  } catch (e) {
    rows.value = []
    if (!background) {
      selected.value = null
      versions.value = []
    }
    error.value = () => (e instanceof Error ? e.message : t('读取失败。'))
  } finally {
    if (!background) busy.value = false
  }
}
async function select(d: Doc) {
  busy.value = true
  error.value = ''
  editing.value = false
  creating.value = false
  selected.value = null
  try {
    const [detail, history] = await Promise.all([
      request<Doc>(`/documents/${d.id}`),
      request<Version[]>(`/documents/${d.id}/versions`),
    ])
    selected.value = detail
    versions.value = history
    title.value = detail.title
    category.value = detail.category
    visibility.value = detail.visibility
    allowedRoles.value = [...detail.allowedRoles]
    file.value = null
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('读取失败。'))
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
  const current = !creating.value && !editing.value ? selected.value : null
  return {
    plantId: props.plantId,
    assetId: selected.value?.assetId ?? props.assetId ?? null,
    title: current?.title ?? title.value,
    category: current?.category ?? category.value,
    visibility: current?.visibility ?? visibility.value,
    allowedRoles: current?.allowedRoles ?? allowedRoles.value,
    version: selected.value?.version ?? 0,
  }
}
async function upload() {
  if (
    creating.value &&
    metadataForm.value &&
    !(await metadataForm.value.validate().catch(() => false))
  )
    return
  if (!file.value) {
    error.value = () => t('请选择文件。')
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
    notice.value = () => t('已上传草稿版本；发布后可授权下载。')
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('上传失败。'))
  } finally {
    busy.value = false
  }
}
async function action(kind: string, number?: number) {
  if (
    kind === 'configure' &&
    metadataForm.value &&
    !(await metadataForm.value.validate().catch(() => false))
  )
    return
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
    notice.value = () => t('文档变更已保存，授权与发布状态立即生效。')
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('保存失败。'))
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
    notice.value = () => t('已通过授权检查并下载。')
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('下载失败。'))
  } finally {
    busy.value = false
  }
}
function editMetadata() {
  if (!selected.value) return
  title.value = selected.value.title
  category.value = selected.value.category
  visibility.value = selected.value.visibility
  allowedRoles.value = [...selected.value.allowedRoles]
  editing.value = true
}
function closeView() {
  if (editing.value) editing.value = false
  else closeDetail()
}
function closeDetail() {
  creating.value = false
  editing.value = false
  selected.value = null
  versions.value = []
  file.value = null
  error.value = ''
}
const metadataDirty = computed(
  () =>
    !!selected.value &&
    editing.value &&
    (title.value !== selected.value.title ||
      category.value !== selected.value.category ||
      visibility.value !== selected.value.visibility ||
      JSON.stringify(allowedRoles.value) !== JSON.stringify(selected.value.allowedRoles)),
)
const metadataForm = ref<FormInstance>()
const metadataModel = computed(() => ({ title: title.value, category: category.value }))
const checking = ref(false)
async function refreshState() {
  if (
    busy.value ||
    checking.value ||
    creating.value ||
    editing.value ||
    file.value ||
    props.active === false
  )
    return
  checking.value = true
  try {
    if (selected.value) {
      const id = selected.value.id
      const [detail, history] = await Promise.all([
        request<Doc>(`/documents/${id}`),
        request<Version[]>(`/documents/${id}/versions`),
      ])
      // Do not apply a response after the user starts editing or leaves the detail.
      if (selected.value?.id === id && !editing.value && !file.value) {
        selected.value = detail
        versions.value = history
      }
    } else await load(false, true)
  } catch (e) {
    if (e instanceof ApiError && [401, 403, 404].includes(e.status)) {
      selected.value = null
      versions.value = []
    }
    error.value = () => (e instanceof Error ? e.message : t('资料状态读取失败。'))
  } finally {
    checking.value = false
  }
}
onMounted(() => load())
const unsubscribe = subscribePlantEvents((event) => {
  if (!event || (event.plantId === props.plantId && event.eventType === 'DocumentsChanged'))
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
  <section class="asset-detail">
    <div class="section-title">
      <div>
        <p class="eyebrow">KNOWLEDGE / DOCUMENTS</p>
        <h2>{{ t('设备与工厂资料') }}</h2>
      </div>
      <div class="actions">
        <el-button :disabled="busy" @click="load()">{{ t('刷新资料') }}</el-button
        ><el-button v-if="writer && !archived" :disabled="busy" @click="prepare" type="primary">{{
          t('上传新资料')
        }}</el-button>
      </div>
    </div>
    <el-alert
      v-if="error && !selected && !creating"
      :title="error"
      type="error"
      :closable="false"
      show-icon
    />
    <el-alert v-if="notice" :title="notice" type="success" :closable="false" show-icon />
    <el-table
      :data="rows"
      v-loading="busy"
      row-key="id"
      :empty-text="t('暂无可访问资料')"
      @row-click="(doc: Doc) => !busy && select(doc)"
    >
      <el-table-column :label="t('资料标题')" min-width="200"
        ><template #default="{ row }"
          ><el-button link type="primary" :disabled="busy" @click.stop="select(row)">{{
            row.title
          }}</el-button></template
        ></el-table-column
      >
      <el-table-column prop="category" :label="t('分类')" min-width="130" />
      <el-table-column :label="t('状态')" width="100"
        ><template #default="{ row }"
          ><el-tag :type="row.currentPublishedVersionId ? 'success' : 'info'">{{
            row.currentPublishedVersionId ? t('已发布') : t('未发布')
          }}</el-tag></template
        ></el-table-column
      >
      <el-table-column :label="t('读取范围')" min-width="160"
        ><template #default="{ row }">{{
          row.visibility === 'PlantReaders' ? t('工厂读者') : row.allowedRoles.join(' / ')
        }}</template></el-table-column
      >
      <el-table-column prop="version" :label="t('版本')" width="80" />
    </el-table>
    <el-button v-if="cursor" :disabled="busy" @click="load(true)" type="primary">{{
      t('加载更多资料')
    }}</el-button>
    <SidePanel
      v-if="selected || creating"
      :mode="creating || editing || assetId ? 'dialog' : 'drawer'"
      :title="creating ? t('上传新资料') : editing ? t('编辑资料信息') : selected!.title"
      :subtitle="t('知识资料 / 详情')"
      :busy="busy"
      :dirty="metadataDirty || !!file || creating"
      @close="closeView"
    >
      <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
      <el-alert v-if="notice" :title="notice" type="success" :closable="false" show-icon />
      <el-form
        ref="metadataForm"
        :model="metadataModel"
        scroll-to-error
        label-position="top"
        v-if="creating || (selected && writer && editing)"
        class="editor-panel"
        @submit.prevent="creating ? upload() : action('configure')"
      >
        <h3>{{ creating ? t('上传资料') : t('资料信息与读取权限') }}</h3>
        <el-form-item
          :label="t('资料标题')"
          prop="title"
          :rules="[{ required: true, whitespace: true, message: t('请填写此项'), trigger: 'blur' }]"
          ><el-input v-model="title" required maxlength="200" /></el-form-item
        ><el-form-item
          :label="t('分类')"
          prop="category"
          :rules="[{ required: true, whitespace: true, message: t('请填写此项'), trigger: 'blur' }]"
          ><el-input v-model="category" required maxlength="64" /></el-form-item
        ><el-form-item :label="t('读取范围')"
          ><el-select filterable v-model="visibility">
            <el-option value="PlantReaders" :label="t('工厂读者')" />
            <el-option value="RestrictedRoles" :label="t('指定角色')" /> </el-select
        ></el-form-item>
        <el-checkbox-group
          v-if="visibility === 'RestrictedRoles'"
          v-model="allowedRoles"
          :aria-label="t('允许读取的角色')"
        >
          <el-checkbox
            v-for="r in ['Viewer', 'Operator', 'MaintenanceEngineer', 'Admin']"
            :key="r"
            class="check-label"
            :value="r"
            >{{ r }}</el-checkbox
          >
        </el-checkbox-group>
        <el-form-item v-if="creating" :label="t('文件（PDF / Markdown / UTF-8 TXT，最多 10 MiB）')"
          ><input
            type="file"
            accept=".pdf,.md,.txt"
            @change="file = ($event.target as HTMLInputElement).files?.[0] ?? null" /></el-form-item
        ><el-button :disabled="busy" native-type="submit" type="primary">{{
          creating ? t('上传草稿') : t('保存资料信息')
        }}</el-button>
      </el-form>
      <section v-if="selected && !editing" class="editor-panel">
        <div class="section-title">
          <h3>{{ selected.title }}</h3>
          <div v-if="writer" class="actions">
            <el-button :disabled="busy" @click="editMetadata"> {{ t('编辑资料信息') }}</el-button
            ><el-button :disabled="busy" @click="action('archive')">{{ t('归档资料') }}</el-button>
          </div>
        </div>
        <p>
          {{
            selected.visibility === 'PlantReaders'
              ? t('授权工厂读者可读')
              : t('限定角色：') + selected.allowedRoles.join(' / ')
          }}
          {{ t('· 元数据 v') }}{{ selected.version }}
        </p>
        <el-form label-position="top" v-if="writer" @submit.prevent="upload">
          <el-form-item :label="t('上传下一版本')"
            ><input
              type="file"
              accept=".pdf,.md,.txt"
              @change="
                file = ($event.target as HTMLInputElement).files?.[0] ?? null
              " /></el-form-item
          ><el-button :disabled="busy" native-type="submit" type="primary">{{
            t('保存新版本草稿')
          }}</el-button>
        </el-form>
        <article v-for="v in versions" :key="v.id" class="document-version">
          <h4>{{ t('版本') }} {{ v.versionNumber }} · {{ v.status }}</h4>
          <p>
            {{ v.fileName }} · {{ (v.sizeBytes / 1024).toFixed(1) }} KiB ·
            {{ at(v.uploadedAt) }}
          </p>
          <p class="path-note">
            {{
              v.indexStatus === 'NeedsText'
                ? t('PDF 尚无提取文本；可下载，尚不能用于证据查询。')
                : v.indexStatus === 'Ready'
                  ? t('文本片段已就绪；当前发布版本可供授权证据查询。')
                  : v.indexStatus === 'NotRequested'
                    ? t('旧资料将在授权查询时生成文本片段。')
                    : t('资料为空、超出解析上限或暂不可解析，未纳入证据查询。')
            }}<br />SHA-256 {{ v.sha256 }}
          </p>
          <div class="actions">
            <el-button
              v-if="v.status === 'Published' && selected.currentPublishedVersionId"
              :disabled="busy"
              @click="download(v)"
              type="primary"
            >
              {{ t('下载版本') }} {{ v.versionNumber }}</el-button
            ><el-button
              v-if="writer && v.status === 'Draft'"
              :disabled="busy"
              @click="action('publish', v.versionNumber)"
              type="primary"
            >
              {{ t('发布版本') }} {{ v.versionNumber }}</el-button
            ><el-button
              v-if="writer && v.status === 'Published'"
              :disabled="busy"
              @click="action('withdraw', v.versionNumber)"
            >
              {{ t('撤回版本') }} {{ v.versionNumber }}
            </el-button>
          </div>
        </article>
      </section>
    </SidePanel>
  </section>
</template>
