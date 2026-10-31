import { locale } from './i18n'
import { formatEvidenceText } from './time'

export type EvidenceSource = { kind?: string; title?: string; facts?: readonly string[] }
type Template = readonly [source: string, english: string, localizedSlots?: readonly number[]]

const terms: Record<string, string> = {
  'IndustrialSim P-101 · 仿真设备 / 真实 OPC UA':
    'IndustrialSim P-101 · Simulated device / real OPC UA',
  未记录: 'Not recorded',
  未提供: 'Not provided',
  不可查询: 'Unavailable',
  未登记: 'Not registered',
  未知: 'Unknown',
  无值: 'No value',
  已确认: 'Acknowledged',
  未确认: 'Unacknowledged',
  可信: 'Trusted',
  部分可信: 'Partially trusted',
  不可验证: 'Unverifiable',
  存在: 'Present',
  未发现: 'Not found',
  证据不足: 'Insufficient evidence',
  触发事件窗口: 'Trigger event window',
  恢复事件窗口: 'Recovery event window',
  触发与恢复事件窗口: 'Trigger and recovery event window',
  调查当前窗口: 'Current investigation window',
  触发事件原始窗口: 'Raw trigger event window',
  当前原始窗口: 'Raw current window',
  缺口标记: 'Gap flag',
  时间异常: 'Time anomaly',
  '低质量（Bad/Uncertain）': 'Low quality (Bad/Uncertain)',
  '聚合不可用或无观测（未聚合/清理/未入库待核实）':
    'Aggregate unavailable or no observations (aggregation, retention, or storage status unverified)',
  '窗口起始边界分钟含缺口/不可用标记（具体边界覆盖待核实）':
    'Gap or unavailable flag in the first boundary minute (exact coverage unverified)',
  '窗口结束边界分钟含缺口/不可用标记（具体边界覆盖待核实）':
    'Gap or unavailable flag in the last boundary minute (exact coverage unverified)',
  '与事件区间重叠（分钟级定位）': 'Overlaps the event interval (minute-level positioning)',
  不与事件区间重叠: 'Does not overlap the event interval',
  '同设备候选超过自动读取上限，目录并非完整现场关联清单。':
    'Same-asset candidates exceed the automatic read limit; this directory is not a complete list of physical relations.',
}
const escapeRegex = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

// Match complete, known server templates. Captured values are opaque: numbers,
// timestamps, JSON snapshots, IDs and user-authored names are never rewritten.
function compile([source, english, localizedSlots = []]: Template) {
  const slots = new Map<number, number>()
  let pattern = '^',
    end = 0
  for (const match of source.matchAll(/\{(\d+)\}/g)) {
    pattern += escapeRegex(source.slice(end, match.index))
    const slot = Number(match[1]),
      existing = slots.get(slot)
    if (existing) pattern += `\\${existing}`
    else {
      slots.set(slot, slots.size + 1)
      pattern += '([\\s\\S]*?)'
    }
    end = match.index + match[0].length
  }
  const regex = new RegExp(pattern + escapeRegex(source.slice(end)) + '$', 'u')
  return (value: string): string | undefined => {
    const match = regex.exec(value)
    if (!match) return
    return english.replace(/\{(\d+)\}/g, (_, id: string) => {
      const slot = Number(id),
        captured = match[slots.get(slot)!]
      return localizedSlots.includes(slot) ? (terms[captured] ?? captured) : captured
    })
  }
}
const groups = new Map<string, ReturnType<typeof compile>[]>()
function templates(kinds: readonly string[], entries: readonly Template[]) {
  const rules = entries.map(compile)
  for (const kind of kinds) groups.set(kind, [...(groups.get(kind) ?? []), ...rules])
}

templates(
  ['Alarm'],
  [
    [
      '触发值为 {0} {1}，触发时间为 {2}；这是历史告警快照。',
      'Trigger value: {0} {1}; triggered at {2}. This is a historical alarm snapshot.',
    ],
    [
      '调查取数时告警状态为 {0}，恢复时间为 {1}，人工确认状态为 {2}；确认不代表恢复，恢复不代表根因已消除。',
      'Alarm status when evidence was retrieved: {0}; recovered at: {1}; manual acknowledgment: {2}. Acknowledgment does not mean recovery; recovery does not mean the root cause has been eliminated.',
      [1, 2],
    ],
    [
      '告警规则历史快照：{0}；仅解释本事件，不作为现场操作参数。',
      'Historical alarm rule snapshot: {0}. This explains this event only and must not be used as field operating parameters.',
    ],
  ],
)
templates(
  ['EventDerivedTrend', 'CurrentDerivedTrend'],
  [
    [
      '测点 {0} 的{1}有序原始观测：{2} 至 {3}，首值 {4} {5}，末值 {6} {5}，变化量 {7} {5}，观测间平均变化率 {8} {5}/min，样本数 {9}；仅代表版本已核对且段内无已知缺口的已入库观测，不证明采样间物理连续。',
      'Point {0}, {1}, ordered raw observations: {2} to {3}; first {4} {5}, last {6} {5}, change {7} {5}, average change rate between observations {8} {5}/min, samples {9}. Configuration versions are verified and no internal gap is known; physical continuity between samples is not established.',
      [1],
    ],
    [
      '测点 {0} 的{1}非下降观测段：{2} 至 {3}，首值 {4} {5}，末值 {6} {5}，变化量 {7} {5}，观测间平均变化率 {8} {5}/min，样本数 {9}，严格上升次数 {10}；允许平台值，仅限该段，不确认根因或设备安全。',
      'Point {0}, {1}, non-decreasing observation segment: {2} to {3}; first {4} {5}, last {6} {5}, change {7} {5}, average change rate between observations {8} {5}/min, samples {9}, strict increases {10}. Plateaus are allowed; this applies only to this segment and confirms neither root cause nor asset safety.',
      [1],
    ],
    [
      '测点 {0} 的{1}没有可验证的连续观测段变化；保留样本和限制，不跨坏值、缺口或未知配置计算变化。',
      'Point {0}, {1}: no verifiable change across a continuous observation segment. Samples and limitations are retained; changes are not calculated across bad values, gaps or unknown configuration versions.',
      [1],
    ],
    [
      '原始变化证据范围：测点 {0}，窗口 [{1}, {2})，截至 {3}，实际查询 {4}，关联所选告警 {5}，算法 {6}；查询可能晚于截止时刻，不保证截止前全部样本均已入库，首末样本不等于窗口完整覆盖。',
      'Raw change evidence scope: point {0}, window [{1}, {2}), as of {3}, retrieved at {4}, selected alarm {5}, algorithm {6}. Retrieval may follow the cutoff; all samples before it are not guaranteed to have arrived, and endpoint samples do not establish full window coverage.',
    ],
    [
      '原始变化计算限制代码：{0}；段外未知，分钟极值不证明持续升温，多点共同变化不证明共同原因。',
      'Raw change limitation codes: {0}. Conditions outside the segment are unknown; minute extrema do not establish sustained heating, and concurrent point changes do not establish a common cause.',
    ],
  ],
)
templates(
  ['TriggerReading'],
  [
    [
      '触发样本标识为 {0}，来源标识为 {1}，映射标识为 {2}。',
      'Trigger sample ID: {0}; source ID: {1}; mapping ID: {2}.',
    ],
    [
      '原始触发样本值为 {0} {1}，有效时间为 {2}，质量为 {3}，通信来源为 {4}。',
      'Raw trigger sample: {0} {1}; effective at {2}; quality: {3}; communication source: {4}.',
    ],
    [
      '来源登记名称：{0}；登记名称不证明现场真实性。',
      'Registered source name: {0}. A registered name does not establish authenticity in the field.',
      [0],
    ],
    [
      '协议源时间为 {0}，原始状态码为 {1}。',
      'Protocol source time: {0}; raw status code: {1}.',
      [0, 1],
    ],
  ],
)
templates(
  ['Asset'],
  [
    [
      '设备编号为 {0}，设备名称为 {1}，类型为 {2}。',
      'Asset code: {0}; asset name: {1}; type: {2}.',
    ],
  ],
)
templates(
  ['EventTrend', 'RecoveryTrend', 'Trend'],
  [
    [
      '{0}：点位 {1}，窗口 {2}，单位 {3}；仅含已完成分钟，不能由当前窗口替代事件过程。',
      '{0}: point {1}; window {2}; unit: {3}. Includes completed minutes only; the current window cannot replace the event history.',
      [0],
    ],
    [
      '事件所在分钟尚未结束，未列入完成分钟聚合；事件过程请核对原始样本，不能把此窗口当作完整事件。',
      'The event minute has not ended and is excluded from completed-minute aggregates. Review raw samples for the event history; this window is not the complete event.',
    ],
    [
      '{0}：点位 {1}，已结束分钟 {2} 至 {3} 的最小值为 {4} {5}，最大值为 {6} {5}，可信样本数为 {7}，采样模式为 {8}；仅代表已入库观测。',
      '{0}: point {1}; completed minute from {2} to {3}; minimum: {4} {5}; maximum: {6} {5}; trusted samples: {7}; sampling mode: {8}. Represents stored observations only.',
      [0],
    ],
    [
      '{0}无可用分钟聚合；可能未聚合、已清理或未入库，不能据此断言设备或通信故障。',
      '{0} has no available minute aggregates. They may not be aggregated, may have been removed by retention, or may not be stored; this does not establish an asset or communication failure.',
      [0],
    ],
  ],
)
templates(
  ['EventReadings', 'RecoveryReadings'],
  [
    [
      '点位 {0}，样本 {1}，有效时间 {2}，值 {3} {4}，质量 {5}，时间异常 {6}，前置缺口 {7}，来源 {8}，映射 {9}，映射版本 {10}，协议 {11}；协议源时间不等于物理采样时间。',
      'Point: {0}; sample: {1}; effective at {2}; value: {3} {4}; quality: {5}; time anomaly: {6}; preceding gap: {7}; source: {8}; mapping: {9}; mapping version: {10}; protocol: {11}. Protocol source time is not physical sampling time.',
      [3],
    ],
    [
      '事件原始观测不可查询；使用历史告警快照和可用分钟聚合，不补造事件样本。',
      'Raw event observations are unavailable. Use historical alarm snapshots and available minute aggregates; do not fabricate event samples.',
    ],
  ],
)
templates(
  ['RelatedTrend', 'RelatedComparison'],
  [
    [
      '关联点位 {0}；物理量 {1}；单位 {2}；来源 {3}；映射 {4}；映射版本 {5}；同设备对照候选不等于已确认物理关联。',
      'Related point: {0}; quantity: {1}; unit: {2}; source: {3}; mapping: {4}; mapping version: {5}. A same-asset comparison candidate does not establish a physical relation.',
      [3, 4, 5],
    ],
    [
      '关联点位 {0} 的{1} {2}无可用完成分钟聚合；未聚合、已清理或未入库待核实。',
      'Related point {0}, {1} {2}: no available completed-minute aggregates. Aggregation, retention, and storage status require verification.',
      [1],
    ],
    [
      '关联点位 {0} 的{1} {2}：可用分钟 {3}，最小值 {4} {5}，最大值 {6} {5}，可信样本数 {7}。',
      'Related point {0}, {1} {2}: available minutes: {3}; minimum: {4} {5}; maximum: {6} {5}; trusted samples: {7}.',
      [1],
    ],
    [
      '关联点位 {0} 的{1}；分钟 {2}；最小值 {3}，最大值 {4} {5}；Good {6}，Bad {7}，Uncertain {8}，时间异常 {9}，缺口 {10}，采样模式 {11}。',
      'Related point {0}, {1}; minute: {2}; minimum: {3}; maximum: {4} {5}; Good: {6}; Bad: {7}; Uncertain: {8}; time anomalies: {9}; gap: {10}; sampling mode: {11}.',
      [1, 3, 4],
    ],
    [
      '以上采用与告警点位相同的 UTC 窗口，仅含完成分钟聚合；分钟极值不能证明亚分钟同步、现场工况或因果关系，空窗口不等于测点不存在。',
      'The above uses the same UTC windows as the alarm point and includes completed-minute aggregates only. Minute extrema do not prove sub-minute synchrony, field operating conditions, or causation; an empty window does not mean the point does not exist.',
    ],
  ],
)
templates(
  ['AlarmHistory'],
  [
    [
      '最近三十天查询到 {0} 条设备告警；每次最多检查一百条，人工确认与恢复分别记录。',
      'Found {0} asset alarms in the last thirty days; at most one hundred are checked per query. Manual acknowledgment and recovery are recorded separately.',
    ],
  ],
)
templates(
  ['Maintenance'],
  [
    [
      '没有查询到设备维护工单；不代表现场从未维护。',
      'No asset maintenance work orders were found. This does not mean maintenance has never occurred in the field.',
    ],
    [
      '维护记录：{0}；状态为 {1}；处理结果摘录：{2}。',
      'Maintenance record: {0}; status: {1}; outcome excerpt: {2}.',
    ],
  ],
)
templates(
  ['PointStatus'],
  [
    [
      '测点 {0} 截至 {1}：当前数据{2}，当前异常证据{3}；值 {4} {5}；质量 {6}；仅为启用阈值下的有界比较，不确认持续超限或根因。',
      'Point {0} as of {1}: current data trust: {2}; current anomaly evidence: {3}; value: {4} {5}; quality: {6}. This is a bounded comparison against enabled thresholds only, not confirmation of sustained exceedance or root cause.',
      [2, 3, 4, 6],
    ],
    [
      '测点 {0}；样本 {1}；有效时间 {2}；接收时间 {3}；时间来源 {4}；来源 {5}；当前来源版本 {6}；映射 {7}；映射版本 {8}；采样模式 {9}。',
      'Point: {0}; sample: {1}; effective at: {2}; received at: {3}; time source: {4}; source: {5}; current source version: {6}; mapping: {7}; mapping version: {8}; sampling mode: {9}.',
      [1, 2, 3, 4, 5, 6, 7, 8],
    ],
    [
      '测点 {0} 的判定依据代码：{1}；来源健康 {2}，健康记录是否当前 {3}。会话保活、无通知或旧可信值不能单独证明当前正常，也不能单独证明传感器故障。',
      'Point {0} assessment reason codes: {1}; source health: {2}; health record current: {3}. Session keepalive, silence, or an old trusted value alone proves neither current normal operation nor sensor failure.',
      [2, 3],
    ],
    [
      '测点 {0} 的原始样本所记录来源版本 {1}，测点版本 {2}；当前测点版本 {3}。未知版本不得用当前配置追认。',
      'Point {0} raw sample records source version {1} and point version {2}; current point version: {3}. Unknown versions must not be retrospectively inferred from the current configuration.',
      [1, 2],
    ],
    [
      '测点 {0} 的启用规则 {1}，版本 {2}；当前值大于 {3} {4} 时为超阈值证据，小于 {5} {4} 时低于恢复界限；本比较不替代持续时间、回差告警状态机。',
      'Point {0}, enabled rule {1}, version {2}: a current value above {3} {4} is threshold-exceedance evidence; below {5} {4} is below the recovery boundary. This comparison does not replace the duration and hysteresis alarm state machine.',
    ],
    [
      '启用规则超过二十条，本次规则范围已截断，不能据已读规则作全范围未发现异常结论。',
      'More than twenty rules are enabled and this rule scope is truncated. The inspected rules cannot establish absence of anomalies across the full scope.',
    ],
  ],
)
templates(
  ['Assessment'],
  [
    [
      '截至 {0}，当前数据可信度：{1}；当前设备异常证据：{2}。仅限已检查的 {3} 个测点及其启用阈值，未检查启用测点 {4} 个；历史窗口另列，不能据此确诊根因或认定设备安全。',
      'As of {0}, current data trust: {1}; current asset anomaly evidence: {2}. Limited to {3} inspected points and their enabled thresholds; {4} enabled points were not inspected. Historical windows are listed separately; this does not establish root cause or asset safety.',
      [1, 2],
    ],
  ],
)
templates(
  ['DataQuality'],
  [
    [
      '资料版本或读取权限已变化，旧调查内容已隐藏，请重新调查。',
      'Document versions or read permissions have changed. Previous investigation content is hidden; start a new investigation.',
    ],
    [
      '当前观测质量、陈旧或采集配置不可验证，不得用历史触发值判断当前状态或恢复。',
      'Current observation quality, freshness, or collection configuration is unverifiable. Historical trigger values must not determine current state or recovery.',
    ],
    [
      '原始触发样本已不可查询，告警证据来自保留快照。',
      'The original trigger sample is unavailable. Alarm evidence comes from retained snapshots.',
    ],
    [
      '历史触发观测来源：{0}；来源登记名称：{1}。',
      'Historical trigger observation source: {0}; registered source name: {1}.',
      [1],
    ],
    [
      '观测来源：{0}；请区分模拟与现场观测。',
      'Observation source: {0}. Distinguish simulated observations from field observations.',
    ],
    [
      '近期告警超过读取上限，数量仅代表已读记录。',
      'Recent alarms exceed the read limit. Counts represent inspected records only.',
    ],
    [
      '维护记录仅发送最多五条摘要，其余记录请人工查阅。',
      'At most five maintenance summaries are supplied. Review the remaining records manually.',
    ],
    [
      '未检索到匹配的当前已发布授权资料，不能据此猜测规程。',
      'No matching currently published, authorized documents were found. Do not infer procedures from this absence.',
    ],
    [
      '双维度判断仅适用于取证时刻的已检查点位和启用阈值；历史事件与完成分钟窗口另列，不能用历史告警推断当前异常，也不确认持续超限、根因或安全。',
      'The two-dimensional assessment covers inspected points and enabled thresholds at evidence retrieval time only. Historical events and completed-minute windows are separate. Historical alarms do not establish a current anomaly, sustained exceedance, root cause, or safety.',
    ],
    [
      '调查取数范围：已自动查询同设备授权点位目录，未找到其他启用的对照候选；不能据此断言现场不存在关联测点。',
      'Evidence scope: the authorized same-asset point directory was queried automatically. No other enabled comparison candidates were found; this does not establish absence of related points in the field.',
    ],
    [
      '调查取数范围：已自动读取告警点位及同设备对照候选 {0} 的事件、恢复及当前完成分钟趋势；具体窗口和缺项见证据。候选按点位编号排序最多两个，不能据此确认物理关联；其余候选、跨设备趋势及现场工况未自动读取。{1}',
      'Evidence scope: event, recovery, and current completed-minute trends for the alarm point and same-asset comparison candidates {0} were retrieved automatically. See evidence for windows and gaps. Up to two candidates are selected by point code; this does not establish a physical relation. Other candidates, cross-asset trends, and field operating conditions were not retrieved automatically. {1}',
      [1],
    ],
    [
      '{0}；点位 {1}；{2}；类型：{3}；{4}。质量缺失不能单独支持传感器或通信故障判断。',
      '{0}; point: {1}; {2}; type: {3}; {4}. Missing quality alone does not support a sensor or communication failure assessment.',
      [0, 3, 4],
    ],
    [
      '触发事件的原始观测不可查询；历史快照和聚合不能替代完整原始过程。',
      'Raw trigger-event observations are unavailable. Historical snapshots and aggregates cannot replace the complete raw process.',
    ],
    [
      '恢复事件的原始观测不可查询；需要保留无法核实的范围及责任接续。',
      'Raw recovery-event observations are unavailable. Preserve the unverifiable scope and responsible handoff.',
    ],
  ],
)

templates(
  ['DataQuality'],
  [
    [
      '趋势窗口不晚于取证截止；完成分钟统计反映实际查询水位，可能含截止后迟到的历史观测，不证明统计在截止前已经可用。原始变化另按接收时间过滤。',
      'Trend windows end by the evidence cutoff. Completed-minute statistics reflect the actual query watermark and may include historical observations arriving after the cutoff; they do not prove the statistics existed before the cutoff. Raw changes are filtered separately by receipt time.',
    ],
    [
      '维护工单创建时间不晚于取证截止；状态与处理结果为实际读取时版本，不能追认为截止时历史状态。',
      'Maintenance work orders were created by the evidence cutoff. Their status and resolution reflect the version at actual retrieval, not a reconstructed historical state at the cutoff.',
    ],
    [
      '近期告警只展开最多五条触发快照；数量最多检查一百条，不代表全部事件过程。',
      'At most five recent alarm trigger snapshots are expanded and at most one hundred records are counted; this does not represent complete event histories.',
    ],
    [
      '检索匹配资料的发布时间晚于取证截止，未用于本次调查。',
      'A matching document was published after the evidence cutoff and was excluded from this investigation.',
    ],
    [
      '按需工具 {0} 本次读取不可用；未取得的证据保持未知，不据此判断设备恢复或正常。',
      'On-demand tool {0} was unavailable for this read. Unretrieved evidence remains unknown and does not establish equipment recovery or normal operation.',
    ],
    [
      '调查取数范围：已捕获取证时刻告警点位及同设备对照候选 {0} 的当前值、质量和来源健康；详细趋势、告警历史、维护和资料按需读取。趋势使用固定事件、恢复及当前窗口，候选按点位编号排序最多两个，不能据此确认物理关联；其余候选、跨设备趋势及现场工况未读取。{1}',
      'Evidence scope: current values, quality and source health were captured for the alarm point and same-asset candidates {0}. Detailed trends, alarm history, maintenance and documents are read on demand. Trends use fixed event, recovery and current windows. Up to two candidates are selected by point code; this does not establish a physical relation. Other candidates, cross-asset trends and field conditions were not read. {1}',
      [1],
    ],
  ],
)
templates(
  ['PointStatus'],
  [
    [
      '历史窗口 {0} [{1}, {2})；实际查询 {3}；数据{4}；Good {5}，Bad {6}，Uncertain {7}；缺口 {8}；仅描述该历史窗口，不替代当前判断。',
      'Historical window {0} [{1}, {2}); retrieved {3}; data trust: {4}; Good {5}, Bad {6}, Uncertain {7}; gap: {8}. This describes the historical window only and does not replace the current assessment.',
      [4],
    ],
  ],
)

const titles: Record<string, Record<string, string>> = {
  Alarm: { 所选告警的历史快照: 'Historical snapshot of the selected alarm' },
  TriggerReading: { 告警原始触发样本与来源: 'Original alarm trigger sample and source' },
  Asset: { 设备资料: 'Asset details' },
  EventReadings: { '事件原始观测（有界节选）': 'Raw event observations (bounded excerpt)' },
  RecoveryReadings: { '事件原始观测（有界节选）': 'Raw recovery observations (bounded excerpt)' },
  RelatedComparison: { 自动关联趋势对照摘要: 'Automatic related-trend comparison summary' },
  AlarmHistory: { 近期告警: 'Recent alarms' },
  Maintenance: { 维护记录摘要: 'Maintenance record summaries' },
  Assessment: {
    '服务端双维度判断（取证时快照）': 'Server two-dimensional assessment (evidence snapshot)',
  },
  DataQuality: {
    服务端数据质量与范围提示: 'Server data quality and scope notices',
    按需取证质量与范围提示: 'On-demand evidence quality and scope notices',
    按需工具读取失败: 'On-demand evidence read failed',
  },
}

export function formatEvidenceTitle(evidence: EvidenceSource): string {
  const title = evidence.title ?? ''
  if (locale.value !== 'en') return title
  const kind = evidence.kind ?? ''
  if (titles[kind]?.[title]) return titles[kind][title]
  if (['EventTrend', 'RecoveryTrend', 'Trend'].includes(kind)) return terms[title] ?? title
  if (kind === 'PointStatus' && title.startsWith('测点证据状态与按需历史窗口 · '))
    return (
      'Point evidence status and requested historical windows · ' +
      title.slice('测点证据状态与按需历史窗口 · '.length)
    )
  if (kind === 'PointStatus' && title.startsWith('测点证据状态 · '))
    return 'Point evidence status · ' + title.slice('测点证据状态 · '.length)
  if (
    (kind === 'EventDerivedTrend' || kind === 'CurrentDerivedTrend') &&
    title.startsWith('原始观测变化 · ')
  )
    return title
      .replace('原始观测变化 · ', 'Raw observation change · ')
      .replace('触发事件原始窗口', 'Raw trigger event window')
      .replace('当前原始窗口', 'Raw current window')
  if (kind === 'RelatedTrend' && title.startsWith('关联点位同期趋势 · '))
    return 'Related point concurrent trends · ' + title.slice('关联点位同期趋势 · '.length)
  if (kind === 'Document')
    return title.replace(/ · 版本 (\d+)(.*?) · 行 (\d+)–(\d+)$/, ' · Version $1$2 · Lines $3–$4')
  return title
}

function englishFact(value: string, kind?: string): string {
  const rules = groups.get(kind ?? '')
  if (!rules) return value
  return value
    .split('\n')
    .map((line) => {
      const note = '锚点所在分钟尚未结束，以上聚合不含锚点所在分钟。'
      const hasNote =
        (kind === 'RelatedTrend' || kind === 'RelatedComparison') && line.endsWith(note)
      const text = hasNote ? line.slice(0, -note.length) : line
      for (const rule of rules) {
        const translated = rule(text)
        if (translated !== undefined)
          return (
            translated +
            (hasNote
              ? ' The anchor minute has not ended and is excluded from these aggregates.'
              : '')
          )
      }
      return line
    })
    .join('\n')
}

export function formatEvidenceFact(value: string, kind?: string): string {
  return formatEvidenceText(locale.value === 'en' ? englishFact(value, kind) : value)
}

const ledgerCache = new WeakMap<
  readonly EvidenceSource[],
  { pattern: RegExp; replacements: Map<string, string> } | null
>()

// A model statement may include a whole, cited original fact. Translate only
// exact ledger spans in one pass, never unknown prose or document excerpts.
export function formatInvestigationText(
  value: string,
  evidence: readonly EvidenceSource[] = [],
): string {
  if (locale.value !== 'en') return formatEvidenceText(value)
  if (!ledgerCache.has(evidence)) {
    const replacements = new Map<string, string>()
    for (const item of evidence)
      for (const fact of item.facts ?? []) {
        if (!fact || item.kind === 'Document') continue
        const translated = englishFact(fact, item.kind)
        if (translated !== fact) replacements.set(fact, translated)
      }
    ledgerCache.set(
      evidence,
      replacements.size
        ? {
            replacements,
            pattern: new RegExp(
              [...replacements.keys()]
                .sort((a, b) => b.length - a.length)
                .map(escapeRegex)
                .join('|'),
              'gu',
            ),
          }
        : null,
    )
  }
  const ledger = ledgerCache.get(evidence)
  return formatEvidenceText(
    ledger ? value.replace(ledger.pattern, (fact) => ledger.replacements.get(fact)!) : value,
  )
}
