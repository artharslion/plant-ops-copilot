import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { computed } from 'vue'
import { evidencePresentationUrl, i18nUrl } from './i18n-loader.mjs'

const { locale, setLocale } = await import(i18nUrl)
const { formatEvidenceFact, formatEvidenceTitle, formatInvestigationText } = await import(
  evidencePresentationUrl
)
const trigger =
  '触发值为 12.6 mm/s，触发时间为 2026-10-27T03:17:17.3750100+00:00；这是历史告警快照。'
const state =
  '调查取数时告警状态为 Recovered，恢复时间为 2026-10-27T03:17:30.3688790+00:00，人工确认状态为 未确认；确认不代表恢复，恢复不代表根因已消除。'
const snapshot =
  '{"Name": "P-101 high vibration", "Unit": "mm/s", "RuleType": "HighThreshold", "ClearBelow": 8, "ClearForMs": 5000, "RaiseAbove": 10, "RaiseForMs": 3000, "MaxSampleGapMs": 2500}'
const rule = `告警规则历史快照：${snapshot}；仅解释本事件，不作为现场操作参数。`
const alarm = Object.freeze({
  id: 'E1',
  kind: 'Alarm',
  title: '所选告警的历史快照',
  facts: Object.freeze([trigger, state, rule]),
})
const scope =
  '调查取数范围：已自动读取告警点位及同设备对照候选 REFERENCE_TEMPERATURE、TEMPERATURE 的事件、恢复及当前完成分钟趋势；具体窗口和缺项见证据。候选按点位编号排序最多两个，不能据此确认物理关联；其余候选、跨设备趋势及现场工况未自动读取。同设备候选超过自动读取上限，目录并非完整现场关联清单。'

test('the reported alarm facts and evidence titles render in English while stored text and hashes remain identical', (context) => {
  context.after(() => setLocale('zh-CN'))
  const original = JSON.stringify(alarm),
    hash = createHash('sha256').update(original).digest('hex')
  setLocale('en')
  assert.equal(formatEvidenceTitle(alarm), 'Historical snapshot of the selected alarm')
  const display = formatEvidenceFact(trigger, 'Alarm')
  assert.equal(
    display,
    'Trigger value: 12.6 mm/s; triggered at 2026-10-27 11:17:17.37501 (Beijing time). This is a historical alarm snapshot.',
  )
  const status = formatEvidenceFact(state, 'Alarm')
  assert.match(status, /status when evidence was retrieved: Recovered/)
  assert.match(status, /manual acknowledgment: Unacknowledged/)
  assert.match(status, /recovery does not mean the root cause has been eliminated/)
  assert.doesNotMatch(status, /\p{Script=Han}/u)
  assert.equal(
    formatEvidenceFact(rule, 'Alarm'),
    `Historical alarm rule snapshot: ${snapshot}. This explains this event only and must not be used as field operating parameters.`,
  )
  assert.equal(JSON.stringify(alarm), original)
  assert.equal(createHash('sha256').update(JSON.stringify(alarm)).digest('hex'), hash)
  assert.equal(formatInvestigationText(trigger, [alarm]), display)
})

test('missing-information scope, scope warnings and source descriptions use the same display translation', (context) => {
  context.after(() => setLocale('zh-CN'))
  setLocale('en')
  const notices = [
    scope,
    '双维度判断仅适用于取证时刻的已检查点位和启用阈值；历史事件与完成分钟窗口另列，不能用历史告警推断当前异常，也不确认持续超限、根因或安全。',
    '近期告警超过读取上限，数量仅代表已读记录。',
  ]
  const ledger = [{ kind: 'DataQuality', facts: notices }]
  for (const notice of notices) {
    assert.equal(formatInvestigationText(notice, ledger), formatEvidenceFact(notice, 'DataQuality'))
    assert.doesNotMatch(formatEvidenceFact(notice, 'DataQuality'), /\p{Script=Han}/u)
  }
  assert.match(formatEvidenceFact(scope, 'DataQuality'), /REFERENCE_TEMPERATURE、TEMPERATURE/)
  assert.match(formatEvidenceFact(scope, 'DataQuality'), /exceed the automatic read limit/)
  const sourceName = 'IndustrialSim P-101 · 仿真设备 / 真实 OPC UA'
  assert.equal(
    formatEvidenceFact(`历史触发观测来源：OpcUa；来源登记名称：${sourceName}。`, 'DataQuality'),
    'Historical trigger observation source: OpcUa; registered source name: IndustrialSim P-101 · Simulated device / real OPC UA.',
  )
  assert.equal(
    formatEvidenceFact('来源登记名称：自定义数据源；登记名称不证明现场真实性。', 'TriggerReading'),
    'Registered source name: 自定义数据源. A registered name does not establish authenticity in the field.',
  )
})

test('language switching updates an existing fact and title without another investigation', (context) => {
  context.after(() => setLocale('zh-CN'))
  const evidence = [alarm]
  const text = computed(() => formatInvestigationText(trigger, evidence))
  const title = computed(() => formatEvidenceTitle(alarm))
  assert.equal(locale.value, 'zh-CN')
  assert.match(text.value, /触发值为 12.6/)
  setLocale('en')
  assert.match(text.value, /^Trigger value: 12.6/)
  assert.match(title.value, /^Historical/)
  setLocale('zh-CN')
  assert.match(text.value, /^触发值为 12.6/)
  assert.equal(title.value, alarm.title)
})

test('only whole known ledger facts are translated in model prose, never unknown text or document excerpts', (context) => {
  context.after(() => setLocale('zh-CN'))
  setLocale('en')
  assert.equal(
    formatInvestigationText('Review: ' + trigger, [alarm]),
    'Review: ' + formatEvidenceFact(trigger, 'Alarm'),
  )
  assert.match(formatInvestigationText('Review: ' + trigger, []), /触发值为/)
  assert.match(formatInvestigationText(trigger, [{ ...alarm, kind: 'Document' }]), /触发值为/)
  assert.equal(
    formatEvidenceFact('未知的新证据句子；编号 E1；值 12.6。', 'Alarm'),
    '未知的新证据句子；编号 E1；值 12.6。',
  )
  assert.equal(
    formatEvidenceTitle({ kind: 'Document', title: '中文手册 · 版本 3 · 检查 · 行 4–8' }),
    '中文手册 · Version 3 · 检查 · Lines 4–8',
  )
})

test('trend, sample, assessment and point facts preserve identifiers, precision, windows and uncertainty', (context) => {
  context.after(() => setLocale('zh-CN'))
  setLocale('en')
  const cases = [
    [
      'EventTrend',
      '触发事件窗口：点位 VIBRATION_RMS，窗口 [2026-10-27T03:17:00Z, 2026-10-27T03:18:00Z)，单位 mm/s；仅含已完成分钟，不能由当前窗口替代事件过程。',
    ],
    [
      'Trend',
      '调查当前窗口：点位 VIBRATION_RMS，已结束分钟 2026-10-27T03:17:00Z 至 2026-10-27T03:18:00Z 的最小值为 4.125 mm/s，最大值为 12.6 mm/s，可信样本数为 59，采样模式为 Periodic；仅代表已入库观测。',
    ],
    [
      'RelatedComparison',
      '关联点位 TEMPERATURE 的触发事件窗口 [2026-10-27T03:17:00Z, 2026-10-27T03:18:00Z)：可用分钟 1，最小值 29 degC，最大值 31 degC，可信样本数 59。锚点所在分钟尚未结束，以上聚合不含锚点所在分钟。',
    ],
    [
      'PointStatus',
      '测点 TEMPERATURE 截至 2026-10-27T03:17:00Z：当前数据不可验证，当前异常证据证据不足；值 无值 degC；质量 Bad；仅为启用阈值下的有界比较，不确认持续超限或根因。',
    ],
    [
      'DataQuality',
      '触发事件窗口；点位 TEMPERATURE；[2026-10-27T03:17:00Z, 2026-10-27T03:18:00Z)；类型：低质量（Bad/Uncertain）；与事件区间重叠（分钟级定位）。质量缺失不能单独支持传感器或通信故障判断。',
    ],
  ]
  for (const [kind, fact] of cases)
    assert.doesNotMatch(formatEvidenceFact(fact, kind), /\p{Script=Han}/u, fact)
  assert.match(
    formatEvidenceFact(cases[1][1], 'Trend'),
    /minimum: 4\.125 mm\/s; maximum: 12\.6 mm\/s; trusted samples: 59/,
  )
  assert.match(formatEvidenceFact(cases[4][1], 'DataQuality'), /start inclusive, end exclusive/)
})
