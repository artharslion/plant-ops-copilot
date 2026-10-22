import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import { compileScript, parse } from '@vue/compiler-sfc'
import { createRenderer, nextTick } from 'vue'
import ts from 'typescript'

const cache = new URL('../node_modules/.cache/', import.meta.url)
await fs.mkdir(cache, { recursive: true })
await fs.writeFile(
  new URL('settings-mocks.mjs', cache),
  `
export const request = (...args) => globalThis.settingsHarness.request(...args)
export const useSession = () => ({ user: { roles: globalThis.settingsHarness.roles } })
`,
)
const source = await fs.readFile(new URL('../src/pages/SettingsPage.vue', import.meta.url), 'utf8')
const { descriptor } = parse(source)
const script = compileScript(descriptor, { id: 'settings' }).content.replace(
  /from ['"]\.\.\/(api|stores\/session)['"]/g,
  "from './settings-mocks.mjs'",
)
const output = ts.transpileModule(script, {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
}).outputText
const file = new URL('settings-page.mjs', cache)
await fs.writeFile(file, output)
const component = (await import(file.href)).default
component.render = () => null
const renderer = createRenderer({
  createComment: () => ({}),
  insert() {},
  remove() {},
  parentNode() {},
  nextSibling() {},
})
const settings = (provider = 'Fake', version = 0) => ({
  provider,
  version,
  realConfigured: true,
  realModel: 'fixture-model',
})
async function mount(t, request, roles = ['Admin']) {
  globalThis.settingsHarness = { request, roles }
  const app = renderer.createApp(component)
  t.after(() => app.unmount())
  app.mount({})
  await new Promise((resolve) => setImmediate(resolve))
  await nextTick()
  return app._instance.setupState
}

test('settings loads without changing mode and saves only provider plus version', async (t) => {
  const calls = []
  const state = await mount(t, async (...args) => {
    calls.push(args)
    return args[1] === 'PUT' ? settings('OpenAiCompatible', 4) : settings('Fake', 3)
  })
  assert.equal(calls.length, 1)
  assert.equal(state.selected, 'Fake')
  state.selected = 'OpenAiCompatible'
  await state.save()
  assert.deepEqual(calls[1], ['/settings/ai', 'PUT', { provider: 'OpenAiCompatible', version: 3 }])
  assert.equal(state.settings.provider, 'OpenAiCompatible')
  assert.match(state.saved, /之后新建/)
})

test('settings keeps last known state on conflict and can refresh', async (t) => {
  const state = await mount(t, async (_, method) => {
    if (method === 'PUT') throw new Error('refresh required')
    return settings()
  })
  state.selected = 'OpenAiCompatible'
  await state.save()
  assert.equal(state.settings.provider, 'Fake')
  assert.equal(state.error, 'refresh required')
  assert.equal(state.saving, false)
  await state.load()
  assert.equal(state.selected, 'Fake')
  assert.equal(state.error, '')
})

test('non-admin settings page does not fetch protected settings', async (t) => {
  let calls = 0
  const state = await mount(t, async () => {
    calls++
    return settings()
  }, ['Viewer'])
  assert.equal(calls, 0)
  assert.equal(state.settings, undefined)
})
