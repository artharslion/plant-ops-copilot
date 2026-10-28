<script setup lang="ts">
import { messageRef, t } from '../i18n'
import { computed, reactive, ref } from 'vue'
import type { FormInstance } from 'element-plus'
import type { DirectoryRecord } from '../api'
const props = defineProps<{
  kind: string
  record: DirectoryRecord | null
  locations: DirectoryRecord[]
  busy: boolean
}>()
const emit = defineEmits<{ save: [input: object]; cancel: [] }>()
const form = reactive({
  code: '',
  name: '',
  version: 0,
  isArchived: false,
  description: '',
  displayTimeZone: 'UTC',
  parentLocationId: '',
  sortOrder: 0,
  locationId: '',
  assetType: 'Pump',
  lifecycleStatus: 'Active',
  criticality: 'Medium',
  manufacturer: '',
  model: '',
  serialNumber: '',
  installedAt: '',
  ...props.record,
})
form.parentLocationId = form.parentLocationId ?? ''
form.locationId = form.locationId ?? ''
const error = messageRef()
const formRef = ref<FormInstance>()
async function submit() {
  if (!(await formRef.value?.validate().catch(() => false))) return
  error.value = ''
  if (form.installedAt && Number.isNaN(Date.parse(form.installedAt))) {
    error.value = () => t('请输入有效的 ISO 安装时间（含时区）。')
    return
  }
  emit('save', {
    ...form,
    parentLocationId: form.parentLocationId || null,
    locationId: form.locationId || null,
    installedAt: form.installedAt ? new Date(form.installedAt).toISOString() : null,
  })
}
const labels = computed<Record<string, string>>(() => ({
  plants: t('工厂'),
  locations: t('位置'),
  assets: t('设备'),
}))
</script>
<template>
  <section class="editor-panel" :aria-label="t('目录编辑')">
    <div class="section-title">
      <h2>{{ record ? t('编辑{0}', [labels[kind]]) : t('新增{0}', [labels[kind]]) }}</h2>
      <el-button native-type="button" :disabled="busy" @click="emit('cancel')">
        {{ t('关闭编辑') }}
      </el-button>
    </div>
    <p class="path-note">
      {{
        record
          ? t('当前版本 {0} · 保存时校验版本，成功后记录审计。', [record.version])
          : t('新记录将写入当前授权范围。')
      }}
    </p>
    <el-form
      ref="formRef"
      :model="form"
      scroll-to-error
      label-position="top"
      class="directory-form"
      @submit.prevent="submit"
    >
      <p v-if="error" class="error-banner wide" role="alert">{{ error }}</p>
      <el-form-item
        :label="t('代码')"
        prop="code"
        :rules="[{ required: true, message: t('请填写此项'), trigger: 'change' }]"
        ><el-input v-model="form.code" required maxlength="64"
      /></el-form-item>
      <el-form-item
        :label="t('名称')"
        prop="name"
        :rules="[{ required: true, message: t('请填写此项'), trigger: 'change' }]"
        ><el-input v-model="form.name" required maxlength="200"
      /></el-form-item>
      <template v-if="kind === 'plants'"
        ><el-form-item
          :label="t('显示时区')"
          prop="displayTimeZone"
          :rules="[{ required: true, message: t('请填写此项'), trigger: 'change' }]"
          ><el-input
            v-model="form.displayTimeZone"
            required
            maxlength="100"
            placeholder="Asia/Shanghai" /></el-form-item
      ></template>
      <template v-if="kind === 'locations'">
        <el-form-item :label="t('父位置')"
          ><el-select filterable v-model="form.parentLocationId">
            <el-option value="" :label="t('无（根位置）')" />
            <el-option
              v-for="location in locations.filter((x) => !x.isArchived && x.id !== record?.id)"
              :key="location.id"
              :value="location.id"
              :label="location.name"
            /> </el-select
        ></el-form-item>
        <el-form-item :label="t('排序')"
          ><el-input v-model.number="form.sortOrder" type="number" step="1"
        /></el-form-item>
      </template>
      <template v-if="kind !== 'assets'"
        ><el-form-item class="wide" :label="t('描述')"
          ><el-input
            type="textarea"
            :rows="3"
            v-model="form.description"
            maxlength="8000" /></el-form-item
      ></template>
      <template v-else>
        <el-form-item
          :label="t('位置')"
          prop="locationId"
          :rules="[{ required: true, message: t('请填写此项'), trigger: 'change' }]"
          ><el-select filterable v-model="form.locationId" required>
            <el-option value="" :label="t('选择同厂位置')" />
            <el-option
              v-for="location in locations.filter((x) => !x.isArchived)"
              :key="location.id"
              :value="location.id"
              :label="location.name"
            /> </el-select
        ></el-form-item>
        <el-form-item
          :label="t('设备类型')"
          prop="assetType"
          :rules="[{ required: true, message: t('请填写此项'), trigger: 'change' }]"
          ><el-input v-model="form.assetType" required maxlength="64"
        /></el-form-item>
        <el-form-item :label="t('生命周期')"
          ><el-select filterable v-model="form.lifecycleStatus">
            <el-option value="Active" label="Active" />
            <el-option value="OutOfService" label="OutOfService" />
            <el-option value="Retired" label="Retired" /> </el-select
        ></el-form-item>
        <el-form-item :label="t('重要程度')"
          ><el-select filterable v-model="form.criticality">
            <el-option value="Low" label="Low" />
            <el-option value="Medium" label="Medium" />
            <el-option value="High" label="High" /> </el-select
        ></el-form-item>
        <el-form-item :label="t('制造商')"
          ><el-input v-model="form.manufacturer" maxlength="200" /></el-form-item
        ><el-form-item :label="t('型号')"
          ><el-input v-model="form.model" maxlength="200"
        /></el-form-item>
        <el-form-item :label="t('序列号')"
          ><el-input v-model="form.serialNumber" maxlength="200" /></el-form-item
        ><el-form-item :label="t('安装时间（含时区）')"
          ><el-input
            v-model="form.installedAt"
            type="text"
            placeholder="2026-10-03T00:00:00Z"
            pattern="\d{4}-\d{2}-\d{2}T.*(Z|[+-]\d{2}:\d{2})"
        /></el-form-item>
      </template>
      <el-checkbox
        v-if="record"
        class="wide check-label"
        v-model="form.isArchived"
        :disabled="record.isArchived"
        >{{ t('归档并保留历史（归档后不支持恢复；须先处理活跃子项）') }}</el-checkbox
      >
      <div class="wide">
        <el-button :disabled="busy" native-type="submit" type="primary">{{
          busy ? t('正在保存…') : t('保存修改')
        }}</el-button>
      </div>
    </el-form>
  </section>
</template>
