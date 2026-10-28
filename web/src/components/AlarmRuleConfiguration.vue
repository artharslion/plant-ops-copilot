<script setup lang="ts">
import { messageRef, t } from '../i18n'
import { onMounted, ref } from 'vue'
import { listAll, request, type DirectoryRecord } from '../api'
import type { components } from '../generated/api'
type Point = components['schemas']['PointOverview']
type Rule = components['schemas']['RuleDto']
const props = defineProps<{ plantId: string; initialAssetId: string }>()
const emit = defineEmits<{ saved: [] }>()
const assets = ref<DirectoryRecord[]>([]),
  assetId = ref(''),
  points = ref<Point[]>([]),
  pointId = ref(''),
  rules = ref<Rule[]>([]),
  selectedId = ref('')
const busy = ref(false),
  blocked = ref(false),
  error = messageRef(),
  notice = messageRef()
const empty = () => ({
  name: 'High vibration',
  raiseAbove: 10,
  clearBelow: 8,
  raiseForMs: 3000,
  clearForMs: 5000,
  maxSampleGapMs: 2500,
  severity: 'High',
  enabled: true,
  version: 0,
})
const input = ref(empty())
function chooseRule() {
  const r = rules.value.find((x) => x.id === selectedId.value)
  blocked.value = r?.hasActiveAlarm ?? false
  input.value = r
    ? {
        name: r.name,
        raiseAbove: Number(r.raiseAbove),
        clearBelow: Number(r.clearBelow),
        raiseForMs: Number(r.raiseForMs),
        clearForMs: Number(r.clearForMs),
        maxSampleGapMs: Number(r.maxSampleGapMs),
        severity: r.severity,
        enabled: r.enabled,
        version: Number(r.version),
      }
    : empty()
}
async function run(action: () => Promise<void>) {
  busy.value = true
  error.value = ''
  notice.value = ''
  try {
    await action()
  } catch (e) {
    error.value = () => (e instanceof Error ? e.message : t('规则读取或保存失败'))
  } finally {
    busy.value = false
  }
}
async function loadRules() {
  selectedId.value = ''
  input.value = empty()
  blocked.value = false
  rules.value = []
  if (pointId.value)
    rules.value = await request<Rule[]>(
      `/assets/${assetId.value}/measurement-points/${pointId.value}/alarm-rules`,
    )
}
async function loadPoints() {
  points.value = []
  pointId.value = ''
  rules.value = []
  selectedId.value = ''
  input.value = empty()
  blocked.value = false
  if (assetId.value) {
    points.value = await request<Point[]>(`/assets/${assetId.value}/measurement-points`)
    pointId.value = points.value[0]?.id ?? ''
    await loadRules()
  }
}
async function save() {
  await run(async () => {
    const path = `/assets/${assetId.value}/measurement-points/${pointId.value}/alarm-rules`
    const saved = await request<Rule>(
      path + (selectedId.value ? '/' + selectedId.value : ''),
      selectedId.value ? 'PATCH' : 'POST',
      input.value,
    )
    rules.value = await request<Rule[]>(path)
    selectedId.value = saved.id
    chooseRule()
    emit('saved')
    notice.value = () => t('规则已保存并记录审计；待触发与待恢复计时已清零。')
  })
}
onMounted(() =>
  run(async () => {
    assets.value = (await listAll(`/assets?plantId=${props.plantId}`)).filter((x) => !x.isArchived)
    assetId.value =
      assets.value.find((x) => x.id === props.initialAssetId)?.id ?? assets.value[0]?.id ?? ''
    await loadPoints()
  }),
)
defineExpose({ busy })
</script>
<template>
  <section class="editor-panel">
    <div class="section-title">
      <div>
        <p class="eyebrow">RULE CONFIGURATION / ADMIN</p>
        <h2>{{ t('让阈值具备明确的时间语义。') }}</h2>
      </div>
    </div>
    <p class="path-note">
      {{
        t(
          '高值必须严格高于触发阈值，低值必须严格低于恢复阈值。Bad、Uncertain、陈旧与缺口只中断计时，不恢复活动告警。阈值采用点位标准单位。',
        )
      }}
    </p>
    <p v-if="error" role="alert" class="error-banner">
      {{ error }} {{ t('发生版本冲突时，请关闭配置、刷新后重新检查。') }}
    </p>
    <p v-if="notice" role="status" class="success-banner">{{ notice }}</p>
    <div class="directory-form">
      <el-form-item :label="t('规则设备')"
        ><el-select filterable v-model="assetId" :disabled="busy" @change="run(loadPoints)">
          <el-option v-if="!assets.length" value="" :label="t('无可配置设备')" />
          <el-option
            v-for="a in assets"
            :key="a.id"
            :value="a.id"
            :label="a.code + ' · ' + a.name"
          /> </el-select></el-form-item
      ><el-form-item :label="t('规则点位')"
        ><el-select filterable v-model="pointId" :disabled="busy" @change="run(loadRules)">
          <el-option v-if="!points.length" value="" :label="t('暂无点位')" />
          <el-option
            v-for="p in points"
            :key="p.id"
            :value="p.id"
            :label="p.code + ' / ' + p.canonicalUnit + (p.enabled ? '' : t('（停用）'))"
          /> </el-select></el-form-item
      ><el-form-item class="wide" :label="t('编辑规则')"
        ><el-select filterable v-model="selectedId" :disabled="busy" @change="chooseRule">
          <el-option value="" :label="t('新增高阈值规则')" />
          <el-option
            v-for="r in rules"
            :key="r.id"
            :value="r.id"
            :label="
              r.name +
              ' · v' +
              r.version +
              (r.enabled ? '' : t(' · 停用')) +
              (r.hasActiveAlarm ? t(' · 有活动告警') : '')
            "
          /> </el-select
      ></el-form-item>
    </div>
    <p v-if="blocked" class="error-banner">
      {{
        t(
          '这条规则存在活动告警，当前不能修改或停用。人工确认不会恢复告警；需要可信低值满足恢复条件。',
        )
      }}
    </p>
    <el-form label-position="top" @submit.prevent="save">
      <fieldset class="directory-form rule-fields" :disabled="busy || blocked || !pointId">
        <el-form-item class="wide" :label="t('规则名称')"
          ><el-input v-model="input.name" required maxlength="200" /></el-form-item
        ><el-form-item :label="t('触发阈值')"
          ><el-input
            v-model.number="input.raiseAbove"
            type="number"
            step="any"
            required /></el-form-item
        ><el-form-item :label="t('恢复阈值')"
          ><el-input
            v-model.number="input.clearBelow"
            type="number"
            step="any"
            required /></el-form-item
        ><el-form-item :label="t('触发持续（毫秒）')"
          ><el-input
            v-model.number="input.raiseForMs"
            type="number"
            min="0"
            max="300000"
            required /></el-form-item
        ><el-form-item :label="t('恢复持续（毫秒）')"
          ><el-input
            v-model.number="input.clearForMs"
            type="number"
            min="0"
            max="300000"
            required /></el-form-item
        ><el-form-item :label="t('最大样本间隔（毫秒）')"
          ><el-input
            v-model.number="input.maxSampleGapMs"
            type="number"
            min="1"
            max="300000"
            required /></el-form-item
        ><el-form-item :label="t('严重程度')"
          ><el-select filterable v-model="input.severity">
            <el-option value="Low" :label="t('Low / 低')" />
            <el-option value="Medium" :label="t('Medium / 中')" />
            <el-option value="High" :label="t('High / 高')" /> </el-select></el-form-item
        ><el-checkbox class="check-label" v-model="input.enabled">{{ t('规则启用') }}</el-checkbox
        ><el-button native-type="submit" type="primary">{{ t('保存告警规则') }}</el-button>
      </fieldset>
    </el-form>
  </section>
</template>
