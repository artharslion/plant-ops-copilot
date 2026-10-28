import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import ts from 'typescript'
import { computed } from 'vue'
import { createPinia } from 'pinia'
import { i18nUrl, transpile, withI18n } from './i18n-loader.mjs'

const load = (suffix) => import(`${i18nUrl}#${suffix}`)
const sourceUrl = async (file) =>
  `data:text/javascript;base64,${Buffer.from(withI18n(transpile(await fs.readFile(new URL(file, import.meta.url), 'utf8')))).toString('base64')}`
const importSource = async (file) => import(await sourceUrl(file))

test('language defaults to Chinese, restores saved English, and handles unavailable storage', async (context) => {
  const originalStorage = globalThis.localStorage
  context.after(() => {
    globalThis.localStorage = originalStorage
  })
  for (const [value, expected] of [
    [null, 'zh-CN'],
    ['en', 'en'],
    ['fr', 'zh-CN'],
  ]) {
    globalThis.localStorage = { getItem: () => value, setItem() {} }
    assert.equal((await load(`initial-${value}`)).locale.value, expected)
  }
  globalThis.localStorage = {
    getItem() {
      throw new Error('blocked')
    },
    setItem() {
      throw new Error('blocked')
    },
  }
  const i18n = await load('blocked')
  assert.equal(i18n.locale.value, 'zh-CN')
  assert.doesNotThrow(() => i18n.setLocale('en'))
  assert.equal(i18n.t('总览'), 'Overview')
})

test('switching updates reactive messages, document language, and saved preference without changing business text', async (context) => {
  const originalStorage = globalThis.localStorage,
    originalDocument = globalThis.document
  context.after(() => {
    globalThis.localStorage = originalStorage
    globalThis.document = originalDocument
  })
  const saved = new Map()
  globalThis.localStorage = {
    getItem: (key) => saved.get(key),
    setItem: (key, value) => saved.set(key, value),
  }
  globalThis.document = { documentElement: { lang: '' } }
  const { locale, setLocale, t, messageRef, localeStorageKey } = await load('switch')
  const title = computed(() => t('资产目录'))
  const notice = messageRef()
  notice.value = () => t('已保存。之后新建的调查将使用此模式。')
  assert.equal(globalThis.document.documentElement.lang, 'zh-CN')
  assert.equal(title.value, '资产目录')
  setLocale('en')
  assert.equal(title.value, 'Asset directory')
  assert.equal(notice.value, 'Saved. New investigations will use this mode.')
  assert.equal(globalThis.document.documentElement.lang, 'en')
  assert.equal(saved.get(localeStorageKey), 'en')
  assert.equal(t('请求失败 ({0})', [409]), 'Request failed (409)')
  assert.equal(t('服务端已配置模型：{0}', ['$& <test>']), 'Server-configured model: $& <test>')
  assert.equal(t('自定义设备名称'), '自定义设备名称')
  setLocale('unsupported')
  assert.equal(locale.value, 'en')
  setLocale('zh-CN')
  assert.equal(title.value, '资产目录')
  assert.match(notice.value, /之后新建/)
  notice.value = ''
  assert.equal(notice.value, '')
})

test('every marked literal has an English translation and matching interpolation parameters', async () => {
  const { default: english } = await importSource('../src/locales/en.ts')
  const placeholders = (value) => [...value.matchAll(/\{\d+\}/g)].map((m) => m[0]).sort()
  for (const [key, value] of Object.entries(english)) {
    assert.ok(value.trim(), `Empty translation: ${key}`)
    assert.doesNotMatch(value, /\p{Script=Han}/u, key)
    assert.deepEqual(placeholders(value), placeholders(key), key)
  }
  const files = await fs.readdir(new URL('../src/', import.meta.url), { recursive: true })
  for (const file of files.filter(
    (file) => /\.(vue|ts)$/.test(file) && !file.startsWith('generated'),
  )) {
    const source = await fs.readFile(
      new URL(`../src/${file.replaceAll('\\', '/')}`, import.meta.url),
      'utf8',
    )
    // Parse scripts and template expressions alike to catch missing literal keys.
    for (const match of source.matchAll(/\bt\(\s*('(?:[^'\\]|\\.)*'|"(?:[^"\\]|\\.)*")/g)) {
      const tree = ts.createSourceFile('key.ts', match[1], ts.ScriptTarget.Latest, true)
      const key = tree.statements[0].expression.text
      assert.ok(Object.hasOwn(english, key), `${file}: ${key}`)
    }
  }
})

test('API failures and timestamp labels follow the selected language while preserving evidence text', async (context) => {
  const { setLocale } = await import(i18nUrl)
  const { request, ApiError } = await importSource('../src/api.ts')
  const { formatDateTime, formatEvidenceText } = await importSource('../src/time.ts')
  const originalFetch = globalThis.fetch
  context.after(() => {
    globalThis.fetch = originalFetch
    setLocale('zh-CN')
  })
  globalThis.fetch = async () => ({ ok: false, status: 403, json: async () => ({}) })
  const error = await request('/plants').catch((error) => error)
  assert.ok(error instanceof ApiError)
  assert.match(error.message, /当前角色/)
  setLocale('en')
  assert.equal(error.message, 'Your role does not have permission to perform this action.')
  assert.equal(formatDateTime('2026-10-27T02:28:00Z'), '2026-10-27 10:28:00 (Beijing time)')
  assert.equal(formatDateTime('invalid'), 'Unknown time')
  assert.equal(
    formatEvidenceText('用户原文 2026-10-27T02:28:00Z'),
    '用户原文 2026-10-27 10:28:00 (Beijing time)',
  )
  assert.equal(
    formatEvidenceText('[2026-10-27T02:28:00Z, 2026-10-27T02:29:00Z)'),
    '2026-10-27 10:28:00 to 2026-10-27 10:29:00 (Beijing time, start inclusive, end exclusive)',
  )
})

test('a session error already displayed on the sign-in page follows language changes', async (context) => {
  const { setLocale } = await import(i18nUrl)
  const originalFetch = globalThis.fetch
  context.after(() => {
    globalThis.fetch = originalFetch
    setLocale('zh-CN')
  })
  globalThis.fetch = async () => {
    throw new Error('offline')
  }
  const source = withI18n(
    transpile(await fs.readFile(new URL('../src/stores/session.ts', import.meta.url), 'utf8')),
  )
    .replace("'../api'", JSON.stringify(await sourceUrl('../src/api.ts')))
    .replace("'pinia'", JSON.stringify(import.meta.resolve('pinia')))
    .replace("'vue'", JSON.stringify(import.meta.resolve('vue')))
  const { useSession } = await import(
    `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`
  )
  const session = useSession(createPinia())
  await assert.rejects(session.load())
  assert.equal(session.error, '无法连接服务，请检查连接后重试。')
  setLocale('en')
  assert.equal(session.error, 'Cannot connect to the service. Check your connection and try again.')
  setLocale('zh-CN')
  assert.equal(session.error, '无法连接服务，请检查连接后重试。')
})
