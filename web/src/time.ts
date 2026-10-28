import { t } from './i18n'
const beijing = new Intl.DateTimeFormat('zh-CN', {
  timeZone: 'Asia/Shanghai',
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hourCycle: 'h23',
})

const timestamp = /\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})/

function localTime(value: string) {
  const date = new Date(value)
  if (!Number.isFinite(date.getTime())) return null
  const parts = Object.fromEntries(beijing.formatToParts(date).map((p) => [p.type, p.value]))
  // Keep meaningful sample precision when displaying evidence; remove trailing zeros.
  const fraction = value.match(/\.(\d+)(?:Z|[+-]\d{2}:\d{2})$/)?.[1]?.replace(/0+$/, '')
  return `${parts.year}-${parts.month}-${parts.day} ${parts.hour}:${parts.minute}:${parts.second}${fraction ? '.' + fraction : ''}`
}

export function formatDateTime(value?: string | null): string {
  if (!value) return '—'
  const display = localTime(value)
  return display ? t('{0}（北京时间）', [display]) : t('时间未知')
}

// Presentation only. Keep stored facts, hashes and citation validation unchanged.
export function formatEvidenceText(value: string): string {
  const range = new RegExp(`\\[(${timestamp.source}),\\s*(${timestamp.source})\\)`, 'g')
  return value
    .replace(range, (original, from: string, to: string) => {
      const start = localTime(from)
      const end = localTime(to)
      return start && end ? t('{0} 至 {1}（北京时间，含起始、不含结束）', [start, end]) : original
    })
    .replace(new RegExp(timestamp.source, 'g'), (original) => {
      const display = localTime(original)
      return display ? t('{0}（北京时间）', [display]) : original
    })
}
