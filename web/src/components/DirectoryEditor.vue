<script setup lang="ts">
import { reactive, ref } from 'vue'
import type { DirectoryRecord } from '../api'
const props = defineProps<{ kind: string; record: DirectoryRecord | null; locations: DirectoryRecord[]; busy: boolean }>()
const emit = defineEmits<{ save: [input: object]; cancel: [] }>()
const form = reactive({ code: '', name: '', version: 0, isArchived: false, description: '', displayTimeZone: 'UTC',
  parentLocationId: '', sortOrder: 0, locationId: '', assetType: 'Pump', lifecycleStatus: 'Active', criticality: 'Medium',
  manufacturer: '', model: '', serialNumber: '', installedAt: '', ...props.record })
form.parentLocationId = form.parentLocationId ?? ''
form.locationId = form.locationId ?? ''
const error = ref('')
function submit() {
  error.value = ''
  if (form.installedAt && Number.isNaN(Date.parse(form.installedAt))) { error.value = '请输入有效的 ISO 安装时间（含时区）。'; return }
  emit('save', { ...form, parentLocationId: form.parentLocationId || null, locationId: form.locationId || null,
    installedAt: form.installedAt ? new Date(form.installedAt).toISOString() : null })
}
const labels: Record<string, string> = { plants: '工厂', locations: '位置', assets: '设备' }
</script>
<template>
  <section class="editor-panel" aria-label="目录编辑">
    <div class="section-title"><h2>{{ record ? '编辑' : '新增' }}{{ labels[kind] }}</h2><button class="secondary" type="button" :disabled="busy" @click="emit('cancel')">关闭编辑</button></div>
    <p class="path-note">{{ record ? `当前版本 ${record.version} · 保存时校验版本，成功后记录审计。` : '新记录将写入当前授权范围。' }}</p>
    <form class="directory-form" @submit.prevent="submit">
      <p v-if="error" class="error-banner wide" role="alert">{{ error }}</p>
      <label>代码<input v-model="form.code" required maxlength="64" /></label>
      <label>名称<input v-model="form.name" required maxlength="200" /></label>
      <template v-if="kind === 'plants'"><label>显示时区<input v-model="form.displayTimeZone" required maxlength="100" placeholder="Asia/Shanghai" /></label></template>
      <template v-if="kind === 'locations'">
        <label>父位置<select v-model="form.parentLocationId"><option value="">无（根位置）</option><option v-for="location in locations.filter(x => !x.isArchived && x.id !== record?.id)" :key="location.id" :value="location.id">{{ location.name }}</option></select></label>
        <label>排序<input v-model.number="form.sortOrder" type="number" step="1" /></label>
      </template>
      <template v-if="kind !== 'assets'"><label class="wide">描述<textarea v-model="form.description" maxlength="8000" rows="3" /></label></template>
      <template v-else>
        <label>位置<select v-model="form.locationId" required><option value="">选择同厂位置</option><option v-for="location in locations.filter(x => !x.isArchived)" :key="location.id" :value="location.id">{{ location.name }}</option></select></label>
        <label>设备类型<input v-model="form.assetType" required maxlength="64" /></label>
        <label>生命周期<select v-model="form.lifecycleStatus"><option>Active</option><option>OutOfService</option><option>Retired</option></select></label>
        <label>重要程度<select v-model="form.criticality"><option>Low</option><option>Medium</option><option>High</option></select></label>
        <label>制造商<input v-model="form.manufacturer" maxlength="200" /></label><label>型号<input v-model="form.model" maxlength="200" /></label>
        <label>序列号<input v-model="form.serialNumber" maxlength="200" /></label><label>安装时间（含时区）<input v-model="form.installedAt" type="text" placeholder="2026-10-03T00:00:00Z" pattern="\d{4}-\d{2}-\d{2}T.*(Z|[+-]\d{2}:\d{2})" /></label>
      </template>
      <label v-if="record" class="wide check-label"><input v-model="form.isArchived" type="checkbox" :disabled="record.isArchived" />归档并保留历史（归档后不支持恢复；须先处理活跃子项）</label>
      <div class="wide"><button :disabled="busy">{{ busy ? '正在保存…' : '保存修改' }}</button></div>
    </form>
  </section>
</template>
