import { computed, readonly, ref, shallowRef, watch } from 'vue'
import en from './locales/en'

export type Locale = 'zh-CN' | 'en'
export const localeStorageKey = 'plantops.locale'

function savedLocale(): Locale {
  try {
    return localStorage.getItem(localeStorageKey) === 'en' ? 'en' : 'zh-CN'
  } catch {
    return 'zh-CN'
  }
}

const currentLocale = ref<Locale>(savedLocale())
export const locale = readonly(currentLocale)

// Defer interface notices until display so switching languages keeps their content
// and form state while updating their translation.
export function messageRef(initial = '') {
  const message = shallowRef<string | (() => string)>(initial)
  return computed({
    get: () => (typeof message.value === 'function' ? message.value() : message.value),
    set: (value: string | (() => string)) => {
      message.value = value
    },
  })
}

export function setLocale(value: string) {
  if (value !== 'zh-CN' && value !== 'en') return
  currentLocale.value = value
  try {
    localStorage.setItem(localeStorageKey, value)
  } catch {
    // Switching still works when browser storage is unavailable.
  }
}

watch(
  currentLocale,
  (value) => {
    if (typeof document !== 'undefined') document.documentElement.lang = value
  },
  { immediate: true, flush: 'sync' },
)

// Source-language keys keep the Chinese copy in one place at each call site.
// Only explicitly marked interface messages are translated, never business records.
export function t(key: string, values: readonly unknown[] = []): string {
  const message = currentLocale.value === 'en' ? (en[key as keyof typeof en] ?? key) : key
  return message.replace(/\{(\d+)\}/g, (placeholder, index: string) =>
    Number(index) < values.length ? String(values[Number(index)] ?? '') : placeholder,
  )
}
