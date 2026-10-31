import fs from 'node:fs'
import ts from 'typescript'
const tree = ts.createSourceFile(
  'catalog.ts',
  fs.readFileSync('src/evidencePresentation.ts', 'utf8'),
  ts.ScriptTarget.Latest,
  true,
)
function literal(n) {
  if (ts.isStringLiteral(n) || ts.isNumericLiteral(n))
    return ts.isNumericLiteral(n) ? Number(n.text) : n.text
  if (ts.isArrayLiteralExpression(n)) return n.elements.map(literal)
  if (ts.isObjectLiteralExpression(n))
    return Object.fromEntries(n.properties.map((p) => [p.name.text, literal(p.initializer)]))
  throw Error(n.getText(tree))
}
const result = { terms: {}, titles: {}, rules: [] }
function visit(n) {
  if (ts.isVariableDeclaration(n) && ['terms', 'titles'].includes(n.name.getText(tree)))
    result[n.name.getText(tree)] = literal(n.initializer)
  if (ts.isCallExpression(n) && n.expression.getText(tree) === 'templates') {
    const [kinds, entries] = n.arguments.map(literal)
    for (const [source, english, localizedSlots = []] of entries)
      result.rules.push({ kinds, source, english, localizedSlots })
  }
  ts.forEachChild(n, visit)
}
visit(tree)
result.labels = {
  模拟模型结果: 'Simulated model result',
  对象与事件摘要: 'Asset and event summary',
  '已自动读取的关联趋势（调查时快照）':
    'Automatically retrieved related trends (investigation snapshot)',
  '数据可信度与设备异常证据（截至取证时间）':
    'Data trust and anomaly evidence (at evidence retrieval)',
  '趋势分析（待人工核查）': 'Trend analysis (requires human verification)',
  按序检查与记录: 'Ordered checks and records',
  '依据：': 'Evidence: ',
  '需记录：': 'Record: ',
  '本项完成条件：': 'Completion criteria: ',
  '本项升级条件：': 'Escalation criteria: ',
  '检查时间、人员、点位/工况、观察值及单位、质量与来源、支持或排除的证据；资料不适用或未取得时明确记录。':
    'Record the inspection time, personnel, point and conditions, observed values and units, quality, source and supporting or excluding evidence. Document unavailable or inapplicable records.',
  '补全检查证据并由维护工程师复核。':
    'Complete the inspection evidence and obtain maintenance engineer review.',
  '证据不足、资料不适用或异常复发时交维护工程师复核。':
    'Escalate insufficient evidence, inapplicable documents or recurring anomalies to a maintenance engineer.',
  '尚无有依据的检查项；由维护人员补充适用资料并审核后再安排。':
    'No grounded checks are available. Maintenance personnel must supply applicable documents and review before scheduling.',
  完成与升级条件: 'Completion and escalation conditions',
  '逐项填写证据及结论，区分历史事件、恢复记录和当前观测；核实资料版本及适用范围。告警恢复本身不等于工单完成或根因消除。':
    'Record evidence and conclusions for each item. Distinguish historical events, recovery records and current observations; verify document versions and applicability. Alarm recovery does not itself mean work order completion or elimination of the root cause.',
  '事件证据不足、再次异常、观测不可信或资料不适用时，记录缺项并交维护工程师复核；不得凭本草稿执行设备控制、修改阈值或改变告警状态。':
    'Document missing evidence and seek maintenance engineer review when event evidence is insufficient, anomalies recur, observations are untrusted or documents are inapplicable. This draft does not authorize equipment control, threshold changes or changes to alarm status.',
  '待补证据：': 'Missing evidence: ',
  '质量与来源提示（去重）': 'Quality and source notices (deduplicated)',
  '其余提示见完整调查证据台账。':
    'See the complete investigation evidence ledger for remaining notices.',
  核查告警事件并记录排查证据: 'Review the alarm event and record investigation evidence',
}
fs.writeFileSync(
  '../src/PlantOps.Infrastructure/AI/EvidenceEnglish.json',
  JSON.stringify(result, null, 2) + '\n',
)
