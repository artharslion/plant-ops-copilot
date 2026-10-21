<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { request } from '../api'
import type { components } from '../generated/api'
type Point = components['schemas']['PointOverview']
type Source = components['schemas']['SourceDto']
type Mapping = components['schemas']['MappingDto']
const props = defineProps<{ assetId: string; plantId: string; points: Point[] }>()
const emit = defineEmits<{ saved: [] }>()
const sources = ref<Source[]>([]),
  mappings = ref<Mapping[]>([]),
  selected = ref('')
const error = ref(''),
  notice = ref(''),
  busy = ref(false)
const point = ref({
  id: '',
  code: 'VIBRATION_RMS',
  name: 'Vibration RMS',
  quantity: 'Vibration',
  canonicalUnit: 'mm/s',
  expectedIntervalMs: 1000,
  staleAfterMs: 5000,
  enabled: true,
  version: 0,
})
const source = ref({
  id: '',
  name: 'Golden Path simulation',
  kind: 'Simulation',
  enabled: true,
  version: 0,
  opcUa: { endpointReference: 'local-simulator', publishingIntervalMs: 1000 },
})
const node = ref({
  namespaceUri: 'urn:plant-ops:simulator',
  identifierType: 'String',
  identifier: 'North.Utilities.P101.VibrationRms',
  samplingIntervalMs: 1000,
  queueSize: 10,
  samplingMode: 'Periodic',
})
const mapping = ref({
  id: '',
  dataSourceId: '',
  sourceKey: 'P101.VibrationRms',
  rawUnit: 'mm/s',
  scale: 1,
  offset: 0,
  enabled: true,
  version: 0,
})
const protocolMapping = computed(
  () => sources.value.find((s) => s.id === mapping.value.dataSourceId)?.kind === 'OpcUa',
)
watch(protocolMapping, (protocol) => {
  if (protocol) mapping.value.enabled = false
})
async function run(action: () => Promise<void>) {
  busy.value = true
  error.value = ''
  notice.value = ''
  try {
    await action()
  } catch (e) {
    error.value = e instanceof Error ? e.message : '配置失败'
  } finally {
    busy.value = false
  }
}
async function loadSources() {
  sources.value = await request<Source[]>(`/plants/${props.plantId}/data-sources`)
}
async function choosePoint() {
  await run(async () => {
    const p = props.points.find((p) => p.id === selected.value)
    point.value = p
      ? {
          id: p.id,
          code: p.code,
          name: p.name,
          quantity: p.quantity,
          canonicalUnit: p.canonicalUnit,
          expectedIntervalMs: Number(p.expectedIntervalMs),
          staleAfterMs: Number(p.staleAfterMs),
          enabled: p.enabled,
          version: Number(p.version),
        }
      : {
          id: '',
          code: 'VIBRATION_RMS',
          name: 'Vibration RMS',
          quantity: 'Vibration',
          canonicalUnit: 'mm/s',
          expectedIntervalMs: 1000,
          staleAfterMs: 5000,
          enabled: true,
          version: 0,
        }
    mappings.value = p
      ? await request<Mapping[]>(
          `/assets/${props.assetId}/measurement-points/${p.id}/source-mappings`,
        )
      : []
    chooseMapping('')
  })
}
function chooseSource(id: string) {
  const s = sources.value.find((x) => x.id === id)
  source.value = s
    ? {
        ...s,
        version: Number(s.version),
        opcUa: {
          endpointReference: s.opcUa?.endpointReference ?? 'local-simulator',
          publishingIntervalMs: Number(s.opcUa?.publishingIntervalMs ?? 1000),
        },
      }
    : {
        id: '',
        name: 'Golden Path simulation',
        kind: 'Simulation',
        enabled: true,
        version: 0,
        opcUa: { endpointReference: 'local-simulator', publishingIntervalMs: 1000 },
      }
}
function chooseMapping(id: string) {
  const m = mappings.value.find((x) => x.id === id)
  if (m && sources.value.find((s) => s.id === m.dataSourceId)?.kind === 'OpcUa') {
    node.value = JSON.parse(m.sourceParameters)
  }
  mapping.value = m
    ? { ...m, scale: Number(m.scale), offset: Number(m.offset), version: Number(m.version) }
    : {
        id: '',
        dataSourceId: sources.value[0]?.id ?? '',
        sourceKey: 'P101.VibrationRms',
        rawUnit: 'mm/s',
        scale: 1,
        offset: 0,
        enabled: sources.value[0]?.kind !== 'OpcUa',
        version: 0,
      }
}
async function savePoint() {
  await run(async () => {
    const p = point.value,
      saved = await request<Point>(
        `/assets/${props.assetId}/measurement-points${p.id ? '/' + p.id : ''}`,
        p.id ? 'PATCH' : 'POST',
        p,
      )
    point.value = { ...p, id: saved.id, version: Number(saved.version) }
    selected.value = saved.id
    emit('saved')
    notice.value = '点位已保存并记录审计。'
  })
}
async function saveSource() {
  await run(async () => {
    const s = source.value,
      saved = await request<Source>(
        `/plants/${props.plantId}/data-sources${s.id ? '/' + s.id : ''}`,
        s.id ? 'PATCH' : 'POST',
        {
          ...s,
          enabled: s.enabled,
          opcUa: s.kind === 'OpcUa' ? s.opcUa : null,
        },
      )
    await loadSources()
    chooseSource(saved.id)
    notice.value =
      saved.kind === 'OpcUa'
        ? 'OPC UA 配置已保存；启用受部署批准的本机运行策略约束，连接状态请在测量面板查看。'
        : '模拟源已保存并记录审计。'
    emit('saved')
  })
}
async function saveMapping() {
  await run(async () => {
    const m = mapping.value,
      saved = await request<Mapping>(
        `/assets/${props.assetId}/measurement-points/${point.value.id}/source-mappings${m.id ? '/' + m.id : ''}`,
        m.id ? 'PATCH' : 'POST',
        {
          ...m,
          enabled: protocolMapping.value ? false : m.enabled,
          opcUa: protocolMapping.value ? node.value : null,
        },
      )
    mapping.value = {
      ...saved,
      scale: Number(saved.scale),
      offset: Number(saved.offset),
      version: Number(saved.version),
    }
    mappings.value = await request<Mapping[]>(
      `/assets/${props.assetId}/measurement-points/${point.value.id}/source-mappings`,
    )
    notice.value = '映射已保存并记录审计。'
    emit('saved')
  })
}
onMounted(() => run(loadSources))
defineExpose({ busy })
</script>
<template>
  <div class="telemetry-configuration">
    <h3>测量配置</h3>
    <p class="path-note">
      仅 Admin 可管理。Simulation 固定采用 GoldenPath
      场景。变更来源或换算前，先停用并保存映射；已有读数后不能更改点位单位。冲突后关闭配置、刷新测量，再重新编辑。
      OPC UA 启用仅限部署批准的本机 Development 演示；一个来源最多 10 点，1
      Hz。连接与数值质量分别展示。
    </p>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <el-alert v-if="notice" :title="notice" type="success" :closable="false" show-icon />
    <el-form label-position="top" class="directory-form" @submit.prevent="savePoint">
      <h4 class="wide">1 / 点位定义</h4>
      <el-form-item class="wide" label="编辑点位"
        ><el-select filterable v-model="selected" :disabled="busy" @change="choosePoint">
          <el-option value="" label="新增点位" />
          <el-option
            v-for="p in points"
            :key="p.id"
            :value="p.id"
            :label="p.code + ' · v' + p.version"
          /> </el-select></el-form-item
      ><el-form-item label="点位编码"
        ><el-input v-model="point.code" required maxlength="64" /></el-form-item
      ><el-form-item label="点位名称"
        ><el-input v-model="point.name" required maxlength="200" /></el-form-item
      ><el-form-item label="量纲"
        ><el-input v-model="point.quantity" required maxlength="64" /></el-form-item
      ><el-form-item label="标准单位"
        ><el-input v-model="point.canonicalUnit" required maxlength="32" /></el-form-item
      ><el-form-item label="期望间隔（毫秒）"
        ><el-input
          v-model.number="point.expectedIntervalMs"
          type="number"
          min="1000"
          max="60000"
          step="1000"
          required /></el-form-item
      ><el-form-item label="陈旧阈值（毫秒）"
        ><el-input
          v-model.number="point.staleAfterMs"
          type="number"
          min="1000"
          max="300000"
          required /></el-form-item
      ><el-checkbox class="check-label" v-model="point.enabled">点位启用</el-checkbox
      ><el-button :disabled="busy" native-type="submit" type="primary">保存点位</el-button>
    </el-form>
    <el-form label-position="top" class="directory-form" @submit.prevent="saveSource">
      <h4 class="wide">2 / 同厂数据源</h4>
      <el-form-item label="选择来源"
        ><el-select
          filterable
          :value="source.id"
          :disabled="busy"
          @change="chooseSource(($event.target as HTMLSelectElement).value)"
        >
          <el-option value="" label="新增数据源" />
          <el-option
            v-for="s in sources"
            :key="s.id"
            :value="s.id"
            :label="s.name + ' · ' + (s.enabled ? '启用' : '停用') + ' · v' + s.version"
          /> </el-select></el-form-item
      ><el-form-item label="来源类型"
        ><el-select
          filterable
          v-model="source.kind"
          :disabled="!!source.id"
          @change="source.enabled = source.kind === 'Simulation'"
        >
          <el-option value="Simulation" label="Simulation" />
          <el-option value="OpcUa" label="OPC UA（受控本机）" /> </el-select
      ></el-form-item>
      <template v-if="source.kind === 'OpcUa'">
        <el-form-item label="批准的端点引用"
          ><el-input v-model="source.opcUa.endpointReference" required maxlength="64"
        /></el-form-item>
        <el-form-item label="发布间隔（毫秒）"
          ><el-input
            v-model.number="source.opcUa.publishingIntervalMs"
            type="number"
            min="1000"
            max="60000"
            required
        /></el-form-item>
        <p class="wide path-note">
          端点由部署配置批准。当前常驻运行仅支持 Development 本机回环、明确批准的节点及 1 Hz
          采集；使用 None 安全模式，仅用于本机演示。
        </p>
      </template>
      <el-form-item label="来源名称"
        ><el-input v-model="source.name" maxlength="200" required /></el-form-item
      ><el-checkbox class="check-label" v-model="source.enabled">来源启用</el-checkbox
      ><el-button :disabled="busy" native-type="submit" type="primary">保存来源</el-button>
    </el-form>
    <el-form
      label-position="top"
      v-if="point.id"
      class="directory-form"
      @submit.prevent="saveMapping"
    >
      <h4 class="wide">3 / {{ point.code }} 的来源映射</h4>
      <el-form-item label="编辑映射"
        ><el-select
          filterable
          :value="mapping.id"
          :disabled="busy"
          @change="chooseMapping(($event.target as HTMLSelectElement).value)"
        >
          <el-option value="" label="新增映射" />
          <el-option
            v-for="m in mappings"
            :key="m.id"
            :value="m.id"
            :label="m.sourceKey + ' · ' + (m.enabled ? '启用' : '停用') + ' · v' + m.version"
          /> </el-select></el-form-item
      ><el-form-item label="数据源"
        ><el-select filterable v-model="mapping.dataSourceId" required>
          <el-option value="" label="选择来源" />
          <el-option
            v-for="s in sources"
            :key="s.id"
            :value="s.id"
            :label="s.name"
          /> </el-select></el-form-item
      ><el-form-item v-if="!protocolMapping" label="来源键"
        ><el-input v-model="mapping.sourceKey" maxlength="128" required /></el-form-item
      ><template v-if="protocolMapping">
        <el-form-item label="Namespace URI"
          ><el-input v-model="node.namespaceUri" maxlength="512" required
        /></el-form-item>
        <el-form-item label="标识符类型"
          ><el-select filterable v-model="node.identifierType">
            <el-option value="String" label="String" />
            <el-option value="Numeric" label="Numeric" />
            <el-option value="Guid" label="Guid" /> </el-select
        ></el-form-item>
        <el-form-item class="wide" label="节点标识符"
          ><el-input v-model="node.identifier" maxlength="512" required
        /></el-form-item>
        <el-form-item label="请求采样间隔（毫秒）"
          ><el-input
            v-model.number="node.samplingIntervalMs"
            type="number"
            min="1000"
            max="60000"
            required
        /></el-form-item>
        <el-form-item label="队列大小"
          ><el-input v-model.number="node.queueSize" type="number" min="1" max="100" required
        /></el-form-item>
        <el-form-item label="采样语义"
          ><el-select filterable v-model="node.samplingMode">
            <el-option value="Periodic" label="周期采样" />
            <el-option value="Changes" label="变化通知（覆盖率未知）" /> </el-select
        ></el-form-item>
      </template>
      <el-form-item label="原始单位"
        ><el-input v-model="mapping.rawUnit" required maxlength="32" /></el-form-item
      ><el-form-item label="比例"
        ><el-input v-model.number="mapping.scale" type="number" step="any" required /></el-form-item
      ><el-form-item label="偏移"
        ><el-input
          v-model.number="mapping.offset"
          type="number"
          step="any"
          required /></el-form-item
      ><el-checkbox class="check-label" v-model="mapping.enabled" :disabled="protocolMapping"
        >映射启用</el-checkbox
      ><el-button :disabled="busy" native-type="submit" type="primary">保存映射</el-button>
    </el-form>
  </div>
</template>
