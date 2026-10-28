import { withI18n } from './i18n-loader.mjs'
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import ts from 'typescript'

const source = await fs.readFile(new URL('../src/time.ts', import.meta.url), 'utf8')
const output = withI18n(
  ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
  }).outputText,
)
const { formatDateTime, formatEvidenceText } = await import(
  `data:text/javascript;base64,${Buffer.from(output).toString('base64')}`
)

test('all timestamps display in Beijing time, independent of browser timezone', () => {
  assert.equal(
    formatDateTime('2026-10-27T02:28:00.0000000+00:00'),
    '2026-10-27 10:28:00（北京时间）',
  )
  assert.equal(formatDateTime('2026-10-27T10:28:00+08:00'), '2026-10-27 10:28:00（北京时间）')
  assert.equal(formatDateTime('2026-10-27T18:28:00Z'), '2026-10-28 02:28:00（北京时间）')
  assert.equal(
    formatDateTime('2026-10-27T02:28:00.1234567Z'),
    '2026-10-27 10:28:00.1234567（北京时间）',
  )
  assert.equal(formatDateTime(null), '—')
  assert.equal(formatDateTime('invalid'), '时间未知')
})

test('historical warning intervals explain exclusive boundaries without changing facts or IDs', () => {
  const warning =
    '调查当前窗口；点位 REFERENCE_TEMPERATURE；[2026-10-27T02:28:00.0000000+00:00, 2026-10-27T02:29:00.0000000+00:00)；不与事件区间重叠。'
  assert.equal(
    formatEvidenceText(warning),
    '调查当前窗口；点位 REFERENCE_TEMPERATURE；2026-10-27 10:28:00 至 2026-10-27 10:29:00（北京时间，含起始、不含结束）；不与事件区间重叠。',
  )
  assert.ok(warning.includes('2026-10-27T02:28:00.0000000+00:00'))
  assert.equal(
    formatEvidenceText('样本 E1；值 12.6 mm/s；2026-10-27T02:28:00Z'),
    '样本 E1；值 12.6 mm/s；2026-10-27 10:28:00（北京时间）',
  )
  assert.equal(formatEvidenceText('未记录恢复时间。'), '未记录恢复时间。')
})
