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
          enabled: s.kind === 'OpcUa' ? false : s.enabled,
          opcUa: s.kind === 'OpcUa' ? s.opcUa : null,
        },
      )
    await loadSources()
    chooseSource(saved.id)
    notice.value =
      saved.kind === 'OpcUa'
        ? 'OPC UA 配置已保存为停用状态；容量及安全接入验收完成后可启用。'
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
</script>
<template>
  <div class="telemetry-configuration">
    <h3>测量配置</h3>
    <p class="path-note">
      仅 Admin 可管理。Simulation 固定采用 GoldenPath
      场景。变更来源或换算前，先停用并保存映射；已有读数后不能更改点位单位。冲突后关闭配置、刷新测量，再重新编辑。
      OPC UA 可预先保存批准的来源和节点配置，当前接入尚未开放。
    </p>
    <p v-if="error" class="error-banner" role="alert">{{ error }}</p>
    <p v-if="notice" class="success-banner" role="status">{{ notice }}</p>
    <form class="directory-form" @submit.prevent="savePoint">
      <h4 class="wide">1 / 点位定义</h4>
      <label class="wide"
        >编辑点位<select v-model="selected" :disabled="busy" @change="choosePoint">
          <option value="">新增点位</option>
          <option v-for="p in points" :key="p.id" :value="p.id">
            {{ p.code }} · v{{ p.version }}
          </option>
        </select></label
      ><label>点位编码<input v-model="point.code" required maxlength="64" /></label
      ><label>点位名称<input v-model="point.name" required maxlength="200" /></label
      ><label>量纲<input v-model="point.quantity" required maxlength="64" /></label
      ><label>标准单位<input v-model="point.canonicalUnit" required maxlength="32" /></label
      ><label
        >期望间隔（毫秒）<input
          v-model.number="point.expectedIntervalMs"
          type="number"
          min="1000"
          max="60000"
          step="1000"
          required /></label
      ><label
        >陈旧阈值（毫秒）<input
          v-model.number="point.staleAfterMs"
          type="number"
          min="1000"
          max="300000"
          required /></label
      ><label class="check-label"><input v-model="point.enabled" type="checkbox" />点位启用</label
      ><button :disabled="busy">保存点位</button>
    </form>
    <form class="directory-form" @submit.prevent="saveSource">
      <h4 class="wide">2 / 同厂数据源</h4>
      <label
        >选择来源<select
          :value="source.id"
          :disabled="busy"
          @change="chooseSource(($event.target as HTMLSelectElement).value)"
        >
          <option value="">新增数据源</option>
          <option v-for="s in sources" :key="s.id" :value="s.id">
            {{ s.name }} · {{ s.enabled ? '启用' : '停用' }} · v{{ s.version }}
          </option>
        </select></label
      ><label
        >来源类型<select
          v-model="source.kind"
          :disabled="!!source.id"
          @change="source.enabled = source.kind === 'Simulation'"
        >
          <option value="Simulation">Simulation</option>
          <option value="OpcUa">OPC UA（预配置）</option>
        </select></label
      >
      <template v-if="source.kind === 'OpcUa'">
        <label
          >批准的端点引用<input v-model="source.opcUa.endpointReference" required maxlength="64"
        /></label>
        <label
          >发布间隔（毫秒）<input
            v-model.number="source.opcUa.publishingIntervalMs"
            type="number"
            min="1000"
            max="60000"
            required
        /></label>
        <p class="wide path-note">采用加密安全策略；端点地址由部署配置批准。当前仅保存停用配置。</p>
      </template>
      <label>来源名称<input v-model="source.name" maxlength="200" required /></label
      ><label class="check-label"
        ><input
          v-model="source.enabled"
          type="checkbox"
          :disabled="source.kind === 'OpcUa'"
        />来源启用</label
      ><button :disabled="busy">保存来源</button>
    </form>
    <form v-if="point.id" class="directory-form" @submit.prevent="saveMapping">
      <h4 class="wide">3 / {{ point.code }} 的来源映射</h4>
      <label
        >编辑映射<select
          :value="mapping.id"
          :disabled="busy"
          @change="chooseMapping(($event.target as HTMLSelectElement).value)"
        >
          <option value="">新增映射</option>
          <option v-for="m in mappings" :key="m.id" :value="m.id">
            {{ m.sourceKey }} · {{ m.enabled ? '启用' : '停用' }} · v{{ m.version }}
          </option>
        </select></label
      ><label
        >数据源<select v-model="mapping.dataSourceId" required>
          <option value="">选择来源</option>
          <option v-for="s in sources" :key="s.id" :value="s.id">{{ s.name }}</option>
        </select></label
      ><label v-if="!protocolMapping"
        >来源键<input v-model="mapping.sourceKey" maxlength="128" required /></label
      ><template v-if="protocolMapping">
        <label>Namespace URI<input v-model="node.namespaceUri" maxlength="512" required /></label>
        <label
          >标识符类型<select v-model="node.identifierType">
            <option>String</option>
            <option>Numeric</option>
            <option>Guid</option>
          </select></label
        >
        <label class="wide"
          >节点标识符<input v-model="node.identifier" maxlength="512" required
        /></label>
        <label
          >请求采样间隔（毫秒）<input
            v-model.number="node.samplingIntervalMs"
            type="number"
            min="1000"
            max="60000"
            required
        /></label>
        <label
          >队列大小<input v-model.number="node.queueSize" type="number" min="1" max="100" required
        /></label>
        <label
          >采样语义<select v-model="node.samplingMode">
            <option value="Periodic">周期采样</option>
            <option value="Changes">变化通知（覆盖率未知）</option>
          </select></label
        >
      </template>
      <label>原始单位<input v-model="mapping.rawUnit" required maxlength="32" /></label
      ><label>比例<input v-model.number="mapping.scale" type="number" step="any" required /></label
      ><label>偏移<input v-model.number="mapping.offset" type="number" step="any" required /></label
      ><label class="check-label"
        ><input
          v-model="mapping.enabled"
          type="checkbox"
          :disabled="protocolMapping"
        />映射启用</label
      ><button :disabled="busy">保存映射</button>
    </form>
  </div>
</template>
