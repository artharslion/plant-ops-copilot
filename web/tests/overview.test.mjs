import { withI18n } from './i18n-loader.mjs'
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import { compileScript, parse } from '@vue/compiler-sfc'
import { createRenderer, nextTick, reactive } from 'vue'
import ts from 'typescript'

const cache = new URL('../node_modules/.cache/', import.meta.url)
await fs.mkdir(cache, { recursive: true })
await fs.writeFile(
  new URL('overview-time.mjs', cache),
  withI18n(
    ts.transpileModule(await fs.readFile(new URL('../src/time.ts', import.meta.url), 'utf8'), {
      compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    }).outputText,
  ),
)
await fs.writeFile(
  new URL('overview-mocks.mjs', cache),
  `
export const request = (...args) => globalThis.overviewHarness.request(...args)
export const listAll = () => globalThis.overviewHarness.plants()
export class ApiError extends Error { constructor(status) { super('denied'); this.status = status } }
export const useSession = () => globalThis.overviewHarness.session
export const useWorkOrderDrawer = () => ({ open: target => { globalThis.overviewHarness.openedOrder = target } })
export const realtimeStatus = 'connected'
export const subscribePlantEvents = callback => { globalThis.overviewHarness.notify = callback; return () => { globalThis.overviewHarness.unsubscribed = true } }
`,
)
const { descriptor } = parse(
  await fs.readFile(new URL('../src/pages/OverviewPage.vue', import.meta.url), 'utf8'),
)
const script = compileScript(descriptor, { id: 'overview' })
  .content.replace(/from ['"]\.\.\/time['"]/g, "from './overview-time.mjs'")
  .replace(
    /from ['"]\.\.\/(api|realtime|stores\/(session|workOrderDrawer))['"]/g,
    "from './overview-mocks.mjs'",
  )
await fs.writeFile(
  new URL('overview-component.mjs', cache),
  withI18n(
    ts.transpileModule(script, {
      compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    }).outputText,
  ),
)
const component = (await import(new URL('overview-component.mjs', cache).href)).default
component.render = () => null
const renderer = createRenderer({
  createComment: () => ({}),
  insert() {},
  remove() {},
  parentNode() {},
  nextSibling() {},
})
const flush = async () => {
  await new Promise((resolve) => setImmediate(resolve))
  await nextTick()
}
const snapshot = (plantId) => ({
  plantId,
  checkedAt: '2026-10-19T01:00:00Z',
  counts: { activeAlarms: 12, unacknowledgedAlarms: 7, openWorkOrders: 8, dataIssues: 4 },
  alarms: [],
  workOrders: [],
  dataIssues: [],
})
async function mount(t, request = async () => snapshot('north'), user = { id: 'operator' }) {
  globalThis.overviewHarness = {
    session: reactive({ user, load: async () => {} }),
    plants: async () => [
      { id: 'north', name: 'North' },
      { id: 'south', name: 'South' },
    ],
    request,
  }
  const originalFetch = globalThis.fetch
  globalThis.fetch = async () => ({ ok: true, text: async () => 'Healthy' })
  const app = renderer.createApp(component)
  const instance = app.mount({})
  t.after(() => {
    app.unmount()
    globalThis.fetch = originalFetch
  })
  await flush()
  return instance.$.setupState
}

test('loads full server counts and includes scope and filters in requests', async (t) => {
  const urls = []
  const state = await mount(t, async (url) => {
    urls.push(url)
    return snapshot('north')
  })
  assert.equal(state.cards[0].value, 12)
  state.mine = true
  state.unacknowledgedOnly = true
  state.workOrderStatus = 'Completed'
  await state.refresh()
  assert.match(urls.at(-1), /mine=true/)
  assert.match(urls.at(-1), /unacknowledgedOnly=true/)
  assert.match(urls.at(-1), /workOrderStatus=Completed/)
  assert.equal(state.cards[0].value, 12)
  assert.equal(state.alarmLink('alarm').query.alarmId, 'alarm')
  assert.equal(state.measurementLink('asset', 'point').query.tab, 'measurements')
  assert.equal(state.measurementLink('asset', 'point').query.pointId, 'point')
})

test('a delayed response from a previous plant cannot replace the current plant', async (t) => {
  const state = await mount(t)
  let resolveOld
  globalThis.overviewHarness.request = (url) =>
    url.includes('/north/')
      ? new Promise((resolve) => {
          resolveOld = resolve
        })
      : Promise.resolve(snapshot('south'))
  const oldRequest = state.refresh(true)
  state.plantId = 'south'
  state.changePlant()
  await flush()
  resolveOld(snapshot('north'))
  await oldRequest
  assert.equal(state.overview.plantId, 'south')
  assert.equal(state.loading, false)
})

test('failed background reads clear counts instead of claiming zero or showing old facts', async (t) => {
  const state = await mount(t)
  globalThis.overviewHarness.request = async () => {
    throw new Error('offline')
  }
  await state.refresh(true)
  assert.equal(state.overview, null)
  assert.equal(state.cards[0].value, undefined)
  assert.equal(state.error, 'offline')
})

test('logout clears all business data and invalidates pending responses', async (t) => {
  const state = await mount(t)
  let resolve
  globalThis.overviewHarness.request = () =>
    new Promise((done) => {
      resolve = done
    })
  const pending = state.refresh(true)
  globalThis.overviewHarness.session.user = null
  await flush()
  resolve(snapshot('north'))
  await pending
  assert.equal(state.overview, null)
  assert.deepEqual(state.plants, [])
  assert.equal(state.plantId, '')
})

test('anonymous home never requests business data', async (t) => {
  let requested = false
  const state = await mount(
    t,
    async () => {
      requested = true
    },
    null,
  )
  assert.equal(requested, false)
  assert.equal(state.overview, null)
  assert.equal(state.loading, false)
})
