import { evidencePresentationUrl, i18nUrl, withI18n } from './i18n-loader.mjs'
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import { compileScript, parse } from '@vue/compiler-sfc'
import { createRenderer, createSSRApp, h, nextTick, reactive } from 'vue'
import { renderToString } from 'vue/server-renderer'
import ts from 'typescript'

// Exercise the actual component setup and lifecycle without a browser or new dependencies.
const cache = new URL('../node_modules/.cache/', import.meta.url)
await fs.mkdir(cache, { recursive: true })
await fs.writeFile(
  new URL('panel-time.mjs', cache),
  withI18n(
    ts.transpileModule(await fs.readFile(new URL('../src/time.ts', import.meta.url), 'utf8'), {
      compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    }).outputText,
  ),
)
await fs.writeFile(
  new URL('panel-mocks.mjs', cache),
  `
export const useRouter = () => ({ push: async (to) => globalThis.panelHarness.navigation.push(to), replace: async (to) => globalThis.panelHarness.navigation.push(to) })
export const useRoute = () => globalThis.panelHarness.route
export const useWorkOrderDrawer = () => globalThis.panelHarness.drawer
export const useLeaveProtection = (dirty, busy) => { globalThis.panelHarness.protection = { dirty, busy } }
export const request = (...args) => globalThis.panelHarness.request(...args)
export const downloadDocument = () => {}
export class ApiError extends Error { constructor(status) { super('denied'); this.status = status } }
export const useSession = () => ({ user: { roles: ['Admin'] } })
export const subscribePlantEvents = (callback) => {
  globalThis.panelHarness.notify = callback
  return () => globalThis.panelHarness.onUnsubscribe?.()
}
`,
)
const components = {}
for (const name of [
  'WorkOrderPanel',
  'DocumentPanel',
  'MeasurementPanel',
  'SidePanel',
  'DirectoryEditor',
  'ActionProposalPanel',
  'InvestigationPanel',
  'InvestigationAdvicePanel',
  'WorkOrderDrawer',
]) {
  const source = await fs.readFile(
    new URL(`../src/components/${name}.vue`, import.meta.url),
    'utf8',
  )
  const { descriptor } = parse(source)
  const renderAdvice = name === 'InvestigationAdvicePanel'
  let script = compileScript(descriptor, { id: name, inlineTemplate: renderAdvice }).content
  script = script
    .replace(/from ['"]\.\.\/evidencePresentation['"]/g, `from '${evidencePresentationUrl}'`)
    .replace(/from ['"]\.\.\/time['"]/g, "from './panel-time.mjs'")
    .replace(/import (\w+) from ['"].*\.vue['"]/g, 'const $1 = {}')
    .replace(/from ['"]vue-router['"]/g, "from './panel-mocks.mjs'")
    .replace(/from ['"]\.\.\/composables\/useLeaveProtection['"]/g, "from './panel-mocks.mjs'")
    .replace(
      /from ['"]\.\.\/(api|realtime|stores\/(session|workOrderDrawer))['"]/g,
      "from './panel-mocks.mjs'",
    )
  const output = withI18n(
    ts.transpileModule(script, {
      compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    }).outputText,
  )
  const path = new URL(`panel-${name}.mjs`, cache)
  await fs.writeFile(path, output)
  const component = (await import(path.href)).default
  if (!renderAdvice) component.render = () => null
  components[name] = component
}
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
test('switching languages updates an open work order without losing form input or reloading', async (context) => {
  const { setLocale } = await import(i18nUrl)
  context.after(() => setLocale('zh-CN'))
  let calls = 0
  const state = await mount(
    context,
    'WorkOrderPanel',
    { assetId: 'asset', plantId: 'plant' },
    async () => {
      calls++
      return { items: [], nextCursor: null }
    },
  )
  state.creating = true
  state.title = '待处理设备 / Pump'
  state.note = '保留原始输入'
  const before = calls
  assert.equal(state.labels.Draft, '草稿')
  setLocale('en')
  await nextTick()
  assert.equal(state.labels.Draft, 'Draft')
  assert.equal(state.steps[0], 'Create task')
  assert.equal(state.title, '待处理设备 / Pump')
  assert.equal(state.note, '保留原始输入')
  assert.equal(calls, before)
  setLocale('zh-CN')
  assert.equal(state.labels.Draft, '草稿')
})
async function mount(t, name, props, request) {
  globalThis.panelHarness = {
    request,
    navigation: [],
    route: reactive({ path: '/alarms', fullPath: '/alarms', query: {} }),
    drawer: reactive({
      target: null,
      open(target) {
        this.target = target
      },
      close() {
        this.target = null
      },
    }),
  }
  const app = renderer.createApp(components[name], props)
  t.after(() => app.unmount())
  app.mount({})
  await flush()
  return app._instance.setupState
}
const order = { id: 'order-1', number: 'WO-1', status: 'InProgress', version: 1, resolution: null }

test('new investigations send the selected language without translating existing results or starting extra runs', async (context) => {
  const { setLocale } = await import(i18nUrl)
  context.after(() => setLocale('zh-CN'))
  const posts = []
  const state = await mount(
    context,
    'InvestigationPanel',
    { alarmId: 'alarm-1' },
    async (path, method) => {
      if (path === '/investigation-capabilities') return { model: 'fixture', externalData: false }
      if (method === 'POST') {
        posts.push(path)
        return {
          id: `run-${posts.length}`,
          language: path.includes('language=en') ? 'en' : 'zh-CN',
          result: { summary: 'Original result' },
        }
      }
      return []
    },
  )
  setLocale('en')
  await state.start()
  assert.equal(posts[0], '/alarms/alarm-1/investigations?language=en')
  assert.equal(state.selected.language, 'en')
  setLocale('zh-CN')
  await nextTick()
  assert.equal(posts.length, 1)
  assert.equal(state.selected.language, 'en')
  assert.equal(state.selected.result.summary, 'Original result')
  await state.start()
  assert.equal(posts[1], '/alarms/alarm-1/investigations?language=zh-CN')
})

test('compact investigation overview follows server assessment and keeps legacy conclusions unclassified', async (t) => {
  const state = await mount(t, 'InvestigationPanel', { alarmId: 'alarm-1' }, async (path) =>
    path === '/investigation-capabilities' ? { model: 'fixture', externalData: false } : [],
  )
  state.runs = [
    {
      id: 'run-1',
      warnings: ['保留完整提示'],
      result: { summary: '原始摘要' },
      assessment: { anomalyEvidence: 'Present' },
    },
  ]
  state.selectedId = 'run-1'
  assert.match(state.overview, /超阈值证据.*仍需核查/)
  state.runs[0].assessment.anomalyEvidence = 'NotFound'
  assert.match(state.overview, /本次检查范围内未发现/)
  state.runs[0].assessment.anomalyEvidence = 'Insufficient'
  assert.match(state.overview, /证据不足/)
  state.runs[0].assessment = null
  assert.match(state.overview, /结合证据核查/)
  assert.equal(state.runs[0].result.summary, '原始摘要')
  assert.deepEqual(state.runs[0].warnings, ['保留完整提示'])
})

test('busy or unsaved maintenance proposals protect the workspace and prevent a new run', async (t) => {
  const writes = []
  const state = await mount(
    t,
    'InvestigationPanel',
    { alarmId: 'alarm-1' },
    async (path, method) => {
      if (path === '/investigation-capabilities') return { model: 'fixture', externalData: false }
      if (method === 'POST') writes.push(path)
      return []
    },
  )
  for (const flags of [
    { busy: false, dirty: true },
    { busy: true, dirty: false },
  ]) {
    state.proposalPanel = flags
    assert.equal(state.proposalBusy, flags.busy)
    assert.equal(state.proposalDirty, flags.dirty)
    assert.equal(state.canStart, false)
    await state.start()
  }
  assert.deepEqual(writes, [])
  state.proposalPanel = { busy: false, dirty: false }
  assert.equal(state.canStart, true)
})

test('run history loads independently of capabilities and loading blocks a new run', async (t) => {
  let resolveRuns
  let capabilitiesAvailable = false
  const runs = new Promise((resolve) => {
    resolveRuns = resolve
  })
  const state = await mount(t, 'InvestigationPanel', { alarmId: 'alarm-1' }, async (path) => {
    if (path !== '/investigation-capabilities') return runs
    if (!capabilitiesAvailable) throw new Error('Configuration unavailable')
    return { model: 'fixture', externalData: false }
  })
  assert.equal(state.loadingRuns, true)
  assert.equal(state.canStart, false)
  resolveRuns([{ id: 'run-1', status: 'Failed', failureCode: 'Timeout' }])
  await flush()
  assert.equal(state.loadingRuns, false)
  assert.equal(state.selected.failureCode, 'Timeout')
  assert.equal(state.capabilitiesError, 'Configuration unavailable')
  assert.equal(state.canStart, false)
  capabilitiesAvailable = true
  await state.loadCapabilities()
  assert.equal(state.capabilitiesError, '')
  assert.equal(state.canStart, true)
})

test('starting requires readable history and no active run, even while viewing a past run', async (t) => {
  const { ApiError } = await import(new URL('panel-mocks.mjs', cache))
  let unavailable = true
  const runs = [
    { id: 'new', status: 'Running' },
    { id: 'old', status: 'Succeeded' },
  ]
  const writes = []
  const state = await mount(
    t,
    'InvestigationPanel',
    { alarmId: 'alarm-1' },
    async (path, method) => {
      if (path === '/investigation-capabilities') return { model: 'fixture', externalData: false }
      if (method === 'POST') writes.push(path)
      if (unavailable) throw new ApiError(503)
      return structuredClone(runs)
    },
  )
  assert.equal(state.loadingRuns, false)
  assert.equal(state.canStart, false)
  await state.start()
  unavailable = false
  await state.refresh()
  state.selectedId = 'old'
  assert.equal(state.active, false)
  assert.equal(state.canStart, false)
  await state.start()
  assert.deepEqual(writes, [])
  runs[0].status = 'Failed'
  await state.refresh()
  assert.equal(state.canStart, true)
})

test('temporary history failure preserves an in-progress proposal while access denial clears the snapshot', async (t) => {
  const { ApiError } = await import(new URL('panel-mocks.mjs', cache))
  let status = 200
  const state = await mount(t, 'InvestigationPanel', { alarmId: 'alarm-1' }, async (path) => {
    if (path === '/investigation-capabilities') return { model: 'fixture', externalData: false }
    if (status !== 200) throw new ApiError(status)
    return [{ id: 'run-1', status: 'Succeeded' }]
  })
  status = 503
  for (const flags of [
    { busy: false, dirty: true },
    { busy: true, dirty: false },
  ]) {
    state.proposalPanel = flags
    state.expanded = { id: 'E1' }
    await state.refresh()
    assert.equal(state.selected.id, 'run-1')
    assert.equal(state.expanded, null)
    assert.equal(state.canStart, false)
  }
  status = 403
  await state.refresh()
  assert.equal(state.selected, null)
  assert.equal(state.canStart, false)
})

test('investigation keeps server point labels and opens their frozen evidence; legacy remains unclassified', async (t) => {
  const calls = []
  const state = await mount(t, 'InvestigationPanel', { alarmId: 'alarm-1' }, async (path) => {
    calls.push(path)
    if (path === '/investigation-capabilities') return { model: 'fixture', externalData: false }
    if (path.includes('/evidence/')) return { id: 'E20', facts: ['取证时快照'] }
    return []
  })
  state.runs = [{ id: 'run-1', warnings: [], assessment: null }]
  state.selectedId = 'run-1'
  assert.equal(state.selected.assessment, null)
  state.runs[0].assessment = {
    asOf: '2026-10-26T00:00:00Z',
    dataTrust: 'Partial',
    anomalyEvidence: 'Present',
    evidenceIds: ['E20'],
    points: [{ pointId: 'temp', reasons: ['QualityUntrusted', 'SilenceDoesNotProveFailure'] }],
  }
  assert.equal(state.trustLabels[state.selected.assessment.dataTrust], '部分可信')
  assert.equal(state.anomalyLabels[state.selected.assessment.anomalyEvidence], '存在')
  assert.match(state.reasons(state.selected.assessment.points[0]), /质量不可信.*静默不证明故障/)
  await state.expand(state.selected.assessment.evidenceIds[0])
  assert.equal(state.expanded.id, 'E20')
  assert.ok(calls.includes('/investigations/run-1/evidence/E20'))
  assert.equal(state.selected.assessment.asOf, '2026-10-26T00:00:00Z')
})

test('investigation usage distinguishes legacy, simulated, reported and unknown totals', async (t) => {
  const state = await mount(t, 'InvestigationPanel', { alarmId: 'alarm-1' }, async (path) =>
    path === '/investigation-capabilities' ? { model: 'fixture', externalData: false } : [],
  )
  state.runs = [{ id: 'run-1', warnings: [] }]
  state.selectedId = 'run-1'
  assert.match(state.usageNote, /旧记录.*未知/)
  state.runs[0].runRecord = { usage: { source: 'simulated' } }
  assert.match(state.usageNote, /模拟运行，没有真实模型用量/)
  state.runs[0].runRecord.usage = {
    source: 'provider-reported',
    complete: true,
    actualInputTokens: 20,
    actualOutputTokens: 3,
  }
  assert.match(state.usageNote, /输入 20、输出 3/)
  state.runs[0].runRecord.usage = {
    source: 'provider-reported',
    complete: false,
    reportedInputTokens: 20,
    reportedOutputTokens: 3,
    unknownAttempts: 1,
  }
  assert.match(state.usageNote, /实际总用量未知.*输入 20.*1 次预留未确认/)
  state.runs[0].runRecord.leaseAttempt = 2
  assert.match(state.usageNote, /恢复执行.*仅含本次执行.*之前用量另存/)
})

const proposal = () => ({
  id: 'proposal-1',
  aiRunId: 'run-1',
  plantId: 'plant',
  assetId: 'asset',
  alarmId: 'alarm',
  assetName: 'Pump',
  status: 'Pending',
  version: 1,
  payloadHash: 'a'.repeat(64),
  expiresAt: '2099-10-20T12:00:00Z',
  draft: {
    title: 'Check vibration',
    description: 'Unverified hypothesis; Simulation; [E1]',
    priority: 'Medium',
    assignedToUserId: null,
  },
  investigation: { simulated: true, model: 'fake', warnings: ['gap'], evidence: [] },
  resultEntityId: null,
})

test('note proposal binds selected order/version, saves only note text, and receipt opens the order drawer', async (t) => {
  const writes = []
  let stored
  const state = await mount(
    t,
    'ActionProposalPanel',
    { runId: 'run-1', assetId: 'asset', available: true },
    async (path, method = 'GET', body) => {
      if (path.startsWith('/work-orders?'))
        return {
          items: [
            { ...order, title: 'Inspect pump' },
            { ...order, id: 'closed', status: 'Closed' },
          ],
          nextCursor: null,
        }
      if (method === 'GET')
        return path.endsWith('maintenance-users') ? [] : stored ? [structuredClone(stored)] : []
      writes.push({ path, method, body: structuredClone(body) })
      if (path.endsWith('/note-proposals'))
        stored = {
          ...proposal(),
          actionType: 'AppendWorkOrderNote',
          draft: null,
          noteDraft: { text: 'Investigation\n\n[E1] Check alignment' },
          targetWorkOrderId: body.workOrderId,
          targetWorkOrderVersion: body.workOrderVersion,
          targetWorkOrderNumber: 'WO-1',
          targetWorkOrderTitle: 'Inspect pump',
        }
      if (method === 'PATCH')
        stored = {
          ...stored,
          version: 2,
          payloadHash: 'b'.repeat(64),
          noteDraft: structuredClone(body.draft),
        }
      if (path.endsWith('/execute'))
        stored = { ...stored, status: 'Executed', resultEntityId: 'note-1' }
      return structuredClone(stored)
    },
  )
  await state.loadTargets()
  assert.equal(state.targets.length, 1)
  state.targetId = 'order-1'
  await state.create('AppendWorkOrderNote')
  assert.deepEqual(writes[0].body, { workOrderId: 'order-1', workOrderVersion: 1 })
  assert.equal(state.form.description, 'Investigation\n\n[E1] Check alignment')
  state.form.description = 'Human verified\n\n[E1] Check alignment'
  await state.execute()
  assert.equal(writes.length, 1)
  await state.save()
  assert.equal(writes[1].path, '/action-proposals/proposal-1/note')
  assert.deepEqual(writes[1].body, { version: 1, draft: { text: state.form.description } })
  assert.equal(state.review, true)
  await state.execute()
  assert.equal(writes[2].body.payloadHash, 'b'.repeat(64))
  state.openReceipt()
  assert.deepEqual(
    { ...globalThis.panelHarness.drawer.target },
    { assetId: 'asset', orderId: 'order-1' },
  )
  assert.equal(state.proposal.resultEntityId, 'note-1')
  assert.equal(
    writes.some((write) => write.path === '/work-orders' || write.path.endsWith('/notes')),
    false,
  )
})

test('changed target invalidation removes note text and confirmation; unknown execution recovers same note receipt', async (t) => {
  let stored = {
    ...proposal(),
    actionType: 'AppendWorkOrderNote',
    draft: null,
    noteDraft: { text: 'Unverified check\n[E1]' },
    targetWorkOrderId: 'order-1',
    targetWorkOrderVersion: 1,
  }
  let executions = 0
  const state = await mount(
    t,
    'ActionProposalPanel',
    { runId: 'run-1', available: true },
    async (path, method = 'GET', body) => {
      if (method === 'GET')
        return path.endsWith('maintenance-users') ? [] : [structuredClone(stored)]
      if (method === 'PATCH')
        return structuredClone(
          (stored = { ...stored, version: 2, noteDraft: structuredClone(body.draft) }),
        )
      executions++
      stored = { ...stored, status: 'Executed', resultEntityId: 'original-note' }
      throw new Error('Response lost')
    },
  )
  await state.save()
  await state.execute()
  assert.equal(state.uncertain, true)
  await state.read()
  await state.execute()
  assert.equal(executions, 1)
  assert.equal(state.proposal.resultEntityId, 'original-note')
  stored = { ...stored, status: 'Invalidated', version: 3, noteDraft: null, investigation: null }
  await state.read()
  assert.equal(state.form.description, '')
  assert.equal(state.review, false)
  assert.equal(state.editable, false)
})

test('proposal creation and saving never execute until full confirmation; changed input cancels approval', async (t) => {
  let stored = proposal()
  const writes = []
  const state = await mount(
    t,
    'ActionProposalPanel',
    { runId: 'run-1', available: true },
    async (path, method = 'GET', body) => {
      if (method === 'GET') return path.endsWith('maintenance-users') ? [] : []
      writes.push({
        path,
        method,
        body: body === undefined ? undefined : JSON.parse(JSON.stringify(body)),
      })
      if (method === 'PATCH')
        stored = {
          ...stored,
          version: 2,
          payloadHash: 'b'.repeat(64),
          draft: JSON.parse(JSON.stringify(body.draft)),
        }
      if (path.endsWith('/execute'))
        stored = { ...stored, status: 'Executed', resultEntityId: 'work-order' }
      return structuredClone(stored)
    },
  )
  await state.create()
  await state.execute()
  assert.equal(writes.length, 1)
  state.form.title = 'Human edited task'
  await state.save()
  assert.equal(state.review, true)
  assert.equal(writes.length, 2)
  state.form.title = 'Changed after review'
  await state.execute()
  assert.equal(writes.length, 2)
  state.form.title = 'Human edited task'
  await state.execute()
  assert.equal(writes.length, 3)
  assert.equal(writes[2].body.payloadHash, 'b'.repeat(64))
  assert.equal(writes[2].body.version, 2)
  assert.equal(writes[2].body.draft, undefined)
  assert.equal(state.proposal.resultEntityId, 'work-order')
})

test('proposal polling preserves unsaved text and requires a new review after another session edits', async (t) => {
  let stored = proposal()
  const state = await mount(
    t,
    'ActionProposalPanel',
    { runId: 'run-1', available: true },
    async (path) => (path.endsWith('maintenance-users') ? [] : [structuredClone(stored)]),
  )
  state.form.title = 'Unsaved local text'
  await state.read(true)
  assert.equal(state.form.title, 'Unsaved local text')
  state.review = true
  stored = {
    ...stored,
    version: 2,
    payloadHash: 'b'.repeat(64),
    draft: { ...stored.draft, title: 'Other session title' },
  }
  await state.read(true)
  assert.equal(state.form.title, 'Unsaved local text')
  assert.equal(state.changed, true)
  assert.equal(state.review, false)
  assert.equal(state.editable, false)
  state.accept(stored, true)
  assert.equal(state.form.title, 'Other session title')
  assert.equal(state.dirty, false)
  assert.equal(state.review, false)
})

test('unknown execution result is recovered from the same proposal and never automatically executed again', async (t) => {
  let stored = proposal()
  let executions = 0
  const state = await mount(
    t,
    'ActionProposalPanel',
    { runId: 'run-1', available: true },
    async (path, method = 'GET') => {
      if (method === 'GET')
        return path.endsWith('maintenance-users') ? [] : [structuredClone(stored)]
      executions++
      stored = { ...stored, status: 'Executed', resultEntityId: 'only-work-order' }
      throw new Error('response lost after commit')
    },
  )
  state.review = true
  await state.execute()
  assert.equal(state.uncertain, true)
  assert.equal(state.review, false)
  await state.execute()
  assert.equal(executions, 1)
  await state.read()
  assert.equal(state.proposal.resultEntityId, 'only-work-order')
  assert.equal(state.uncertain, false)
  assert.equal(executions, 1)
})

test('invalidated or unreadable proposal removes old draft and confirmation even with local edits', async (t) => {
  let stored = proposal()
  let denied = false
  const state = await mount(
    t,
    'ActionProposalPanel',
    { runId: 'run-1', available: true },
    async (path) => {
      if (denied) throw new Error('revoked')
      return path.endsWith('maintenance-users') ? [] : [structuredClone(stored)]
    },
  )
  state.form.title = 'Unsaved secret text'
  state.review = true
  stored = { ...stored, status: 'Invalidated', draft: null, investigation: null }
  await state.read(true)
  assert.equal(state.form.title, '')
  assert.equal(state.proposal.draft, null)
  assert.equal(state.review, false)
  denied = true
  await state.read(true)
  assert.equal(state.proposal, null)
  assert.equal(state.items.length, 0)
})

test('failed execution recovery cannot be replaced with a new proposal', async (t) => {
  let disconnected = false
  let posts = 0
  const state = await mount(
    t,
    'ActionProposalPanel',
    { runId: 'run-1', available: true },
    async (path, method = 'GET') => {
      if (method !== 'GET') {
        posts++
        disconnected = true
        throw new Error('response lost')
      }
      if (disconnected) throw new Error('still offline')
      return path.endsWith('maintenance-users') ? [] : [proposal()]
    },
  )
  state.review = true
  await state.execute()
  await state.read()
  assert.equal(state.proposal, null)
  assert.equal(state.uncertain, true)
  await state.create()
  assert.equal(posts, 1)
  assert.equal(state.selectedId, 'proposal-1')
})

test('work order background check preserves form, selection, notes and busy state', async (t) => {
  let current = { ...order }
  const state = await mount(
    t,
    'WorkOrderPanel',
    { assetId: 'asset', plantId: 'plant' },
    async (path) => {
      if (path.includes('?')) return { items: [order], nextCursor: null }
      if (path.endsWith('/notes')) return [{ id: 'note-1', text: 'existing note' }]
      return current
    },
  )
  await state.select(order)
  state.note = 'unsaved maintenance input'
  state.reason = 'keep reason'
  const selected = state.selected
  const notes = state.notes
  current = { ...order, version: 2 }
  const pending = state.refreshState()
  assert.equal(state.busy, false)
  assert.equal(state.selected, selected)
  await pending
  assert.equal(state.selected, selected)
  assert.equal(state.notes, notes)
  assert.equal(state.note, 'unsaved maintenance input')
  assert.equal(state.reason, 'keep reason')
  assert.equal(state.changed, true)
  assert.equal(state.dirty, true)
})

test('quick create defaults to open and draft remains an explicit option', async (t) => {
  const writes = []
  let current = { ...order, status: 'Open', version: 2 }
  const state = await mount(
    t,
    'WorkOrderPanel',
    { assetId: 'asset', plantId: 'plant' },
    async (path, method, body) => {
      if (method === 'POST') {
        writes.push({ path, body })
        return current
      }
      if (path.includes('?')) return { items: [current], nextCursor: null }
      if (path.endsWith('/notes')) return []
      return current
    },
  )
  state.creating = true
  state.title = 'Inspect pump'
  await state.create()
  assert.equal(writes.length, 1)
  assert.equal(writes[0].path, '/work-orders')
  assert.equal(writes[0].body.openImmediately, true)
  assert.equal(state.selected.status, 'Open')
  assert.equal(state.stepIndex, 1)
  assert.equal(state.primaryAction.label, '开始执行')
  current = { ...order, status: 'Draft' }
  state.creating = true
  state.title = 'Plan later'
  await state.create(false)
  assert.equal(writes.length, 2)
  assert.equal(writes[1].body.openImmediately, false)
  assert.equal(state.selected.status, 'Draft')
  assert.equal(state.stepIndex, 0)
})

test('complete and close uses one command; review option preserves Completed', async (t) => {
  const writes = []
  let current = { ...order }
  const state = await mount(
    t,
    'WorkOrderPanel',
    { assetId: 'asset', plantId: 'plant' },
    async (path, method, body) => {
      if (method === 'POST') {
        writes.push(body)
        current = {
          ...current,
          status: body.closeAfterCompletion ? 'Closed' : 'Completed',
          resolution: body.resolution,
          version: current.version + 1,
        }
        return current
      }
      if (path.includes('?')) return { items: [current], nextCursor: null }
      if (path.endsWith('/notes')) return []
      return current
    },
  )
  await state.select(current)
  state.resolution = 'Verified normal vibration'
  await state.mutate('Completed', true)
  assert.equal(writes.length, 1)
  assert.equal(writes[0].closeAfterCompletion, true)
  assert.equal(state.selected.status, 'Closed')
  assert.equal(state.primaryAction, undefined)
  assert.equal(state.stepIndex, 2)
  current = { ...order }
  await state.select(current)
  state.resolution = 'Awaiting review'
  await state.mutate('Completed')
  assert.equal(writes[1].closeAfterCompletion, false)
  assert.equal(state.selected.status, 'Completed')
  assert.equal(state.primaryAction.label, '复核并关闭')
})

test('failed completion keeps the entered result and never retries automatically', async (t) => {
  let writes = 0
  const state = await mount(
    t,
    'WorkOrderPanel',
    { assetId: 'asset', plantId: 'plant' },
    async (path, method) => {
      if (method === 'POST') {
        writes++
        throw new Error('Connection lost')
      }
      if (path.includes('?')) return { items: [order], nextCursor: null }
      if (path.endsWith('/notes')) return []
      return order
    },
  )
  await state.select(order)
  state.resolution = 'Keep this result'
  await state.mutate('Completed', true)
  assert.equal(writes, 1)
  assert.equal(state.selected.status, 'InProgress')
  assert.equal(state.resolution, 'Keep this result')
  assert.equal(state.error, 'Connection lost')
  assert.equal(state.busy, false)
})

test('hidden work-order section and open creation form do not poll', async (t) => {
  let calls = 0
  const state = await mount(
    t,
    'WorkOrderPanel',
    { assetId: 'asset', plantId: 'plant', active: false },
    async () => {
      calls++
      return { items: [], nextCursor: null }
    },
  )
  const initial = calls
  await state.refreshState()
  assert.equal(calls, initial)
  state.creating = true
  state.title = 'keep draft'
  await state.refreshState()
  assert.equal(calls, initial)
  assert.equal(state.title, 'keep draft')
})

test('document background response cannot overwrite metadata being edited', async (t) => {
  const doc = {
    id: 'doc-1',
    title: 'Original',
    category: 'Maintenance',
    visibility: 'PlantReaders',
    allowedRoles: [],
    version: 1,
  }
  let delayed = false
  let resolveDetail
  const state = await mount(t, 'DocumentPanel', { plantId: 'plant' }, async (path) => {
    if (path.includes('?')) return { items: [doc], nextCursor: null }
    if (path.endsWith('/versions')) return []
    if (delayed)
      return new Promise((resolve) => {
        resolveDetail = resolve
      })
    return doc
  })
  await state.select(doc)
  delayed = true
  const pending = state.refreshState()
  state.editMetadata()
  state.title = 'Unsaved title'
  resolveDetail({ ...doc, title: 'Remote title', version: 2 })
  await pending
  assert.equal(state.title, 'Unsaved title')
  assert.equal(state.selected.version, 1)
  assert.equal(state.metadataDirty, true)
  assert.equal(state.busy, false)
})

test('measurement deep link selects the requested abnormal point instead of the default vibration point', async (t) => {
  const paths = []
  const state = await mount(
    t,
    'MeasurementPanel',
    {
      assetId: 'asset',
      plantId: 'plant',
      admin: false,
      archived: false,
      initialPointId: 'temperature',
    },
    async (path) => {
      paths.push(path)
      return path.endsWith('/measurement-points')
        ? [
            { id: 'vibration', code: 'VIBRATION_RMS' },
            { id: 'temperature', code: 'TEMPERATURE' },
          ]
        : null
    },
  )
  assert.equal(state.pointId, 'temperature')
  assert.ok(paths.some((path) => path.includes('pointId=temperature')))
})

test('measurement background refresh never disables controls', async (t) => {
  let resolvePoints
  let delayed = false
  const state = await mount(
    t,
    'MeasurementPanel',
    { assetId: 'asset', plantId: 'plant', admin: false, archived: false },
    async (path) => {
      if (delayed && path.endsWith('/measurement-points'))
        return new Promise((resolve) => {
          resolvePoints = resolve
        })
      return []
    },
  )
  delayed = true
  const pending = state.refresh(true)
  assert.equal(state.busy, false)
  resolvePoints([])
  await pending
  assert.equal(state.busy, false)
})

test('OPC UA connection health stays separate from observation freshness and hides expired connection claims', async (t) => {
  let health = { state: 'Connected', isCurrent: true, reconnectCount: 1 }
  const state = await mount(
    t,
    'MeasurementPanel',
    {
      assetId: 'asset',
      plantId: 'plant',
      admin: false,
      archived: false,
    },
    async (path) => {
      if (path.endsWith('/measurement-points'))
        return [
          {
            id: 'p1',
            dataSourceId: 'source',
            sourceKind: 'OpcUa',
            latestObserved: { isFresh: false },
          },
        ]
      if (path.endsWith('/data-sources')) return [{ id: 'source', enabled: true, health }]
      return null
    },
  )
  assert.equal(state.connectionState(), '已连接')
  assert.equal(state.point.latestObserved.isFresh, false)
  health = { ...health, state: 'Reconnecting' }
  await state.refresh()
  assert.equal(state.connectionState(), '断线重连中')
  health = { ...health, state: 'Connected', isCurrent: false }
  await state.refresh()
  assert.match(state.connectionState(), /状态未确认/)
})

test('dirty panel requests discard confirmation and busy panel cannot close', async (t) => {
  let closes = 0
  const dirty = await mount(
    t,
    'SidePanel',
    { title: 'Edit', dirty: true, onClose: () => closes++ },
    async () => {},
  )
  dirty.close()
  assert.equal(dirty.confirmClose, true)
  assert.equal(closes, 0)
  const busy = await mount(
    t,
    'SidePanel',
    { title: 'Saving', busy: true, onClose: () => closes++ },
    async () => {},
  )
  busy.close()
  assert.equal(busy.confirmClose, false)
  assert.equal(closes, 0)
  busy.discardAndClose()
  assert.equal(closes, 0)
  dirty.discardAndClose()
  assert.equal(dirty.confirmClose, false)
  assert.equal(closes, 1)
})

test('work order entry opens a drawer with alarm association and preserves the current page', async (t) => {
  const state = await mount(
    t,
    'WorkOrderPanel',
    { assetId: 'asset', plantId: 'plant', initialAlarmId: 'alarm' },
    async () => ({ items: [], nextCursor: null }),
  )
  state.openOrder('new')
  assert.deepEqual(globalThis.panelHarness.drawer.target, {
    assetId: 'asset',
    orderId: 'new',
    alarmId: 'alarm',
  })
  assert.deepEqual(globalThis.panelHarness.navigation, [])
})

test('direct create route loads choices, preselects alarm and protects typed input', async (t) => {
  const state = await mount(
    t,
    'WorkOrderPanel',
    { assetId: 'asset', plantId: 'plant', detailId: 'new', initialAlarmId: 'alarm' },
    async (path) => {
      if (path.endsWith('/maintenance-users')) return [{ id: 'user', displayName: 'Engineer' }]
      if (path.startsWith('/alarms?')) return { items: [{ id: 'alarm' }] }
      return { items: [], nextCursor: null }
    },
  )
  assert.equal(state.creating, true)
  assert.equal(state.alarmId, 'alarm')
  state.title = 'Keep entered task'
  assert.equal(globalThis.panelHarness.protection.dirty(), true)
  const calls = []
  globalThis.panelHarness.request = async (...args) => calls.push(args)
  state.createForm = {
    validate: async () => {
      throw new Error('invalid')
    },
  }
  await state.create()
  assert.equal(calls.length, 0)
  assert.equal(state.title, 'Keep entered task')
})

test('directory form rejects invalid fields before saving and preserves concurrency version', async (t) => {
  const saved = []
  const state = await mount(
    t,
    'DirectoryEditor',
    {
      kind: 'assets',
      record: { id: 'asset', code: 'P-01', name: 'Pump', version: 7 },
      locations: [],
      busy: false,
      onSave: (input) => saved.push(input),
    },
    async () => {},
  )
  state.formRef = {
    validate: async () => {
      throw new Error('missing location')
    },
  }
  await state.submit()
  assert.equal(saved.length, 0)
  state.formRef = { validate: async () => true }
  state.form.locationId = 'location'
  await state.submit()
  assert.equal(saved[0].version, 7)
  assert.equal(saved[0].locationId, 'location')
  assert.equal(saved[0].installedAt, null)
})

test('measurement unmount releases its event subscription and refresh timer', async (t) => {
  const originalInterval = globalThis.setInterval
  const intervals = []
  globalThis.setInterval = (...args) => {
    const timer = originalInterval(...args)
    intervals.push(timer)
    return timer
  }
  t.after(() => {
    globalThis.setInterval = originalInterval
  })
  let unsubscribed = 0
  // The same production subscription is closed on component unmount.
  const originalMock = globalThis.panelHarness
  const app = renderer.createApp(components.MeasurementPanel, {
    assetId: 'asset',
    plantId: 'plant',
    admin: false,
    archived: false,
  })
  globalThis.panelHarness = { request: async () => [], onUnsubscribe: () => unsubscribed++ }
  app.mount({})
  await flush()
  assert.ok(intervals.length > 0)
  app.unmount()
  assert.equal(unsubscribed, 1)
  assert.ok(intervals.every((timer) => timer._destroyed))
  globalThis.panelHarness = originalMock
})

test('route leave guard blocks busy work, respects cancellation and cleans refresh protection', async (t) => {
  const source = await fs.readFile(
    new URL('../src/composables/useLeaveProtection.ts', import.meta.url),
    'utf8',
  )
  const compiled = ts
    .transpileModule(source, {
      compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    })
    .outputText.replace(
      /import .* from 'vue';?/,
      'const { onMounted, onBeforeUnmount } = globalThis.leaveHarness;',
    )
    .replace(
      /import .* from 'vue-router';?/,
      'const { onBeforeRouteLeave, onBeforeRouteUpdate, useRouter } = globalThis.leaveHarness;',
    )
    .replace(/import .* from 'element-plus';?/, 'const { ElMessageBox } = globalThis.leaveHarness;')
  const path = new URL('leave-protection-test.mjs', cache)
  await fs.writeFile(path, withI18n(compiled))
  let beforeUnload,
    removed,
    cleanup,
    guard,
    updateGuard,
    prompts = 0,
    cancel = true,
    globalGuard,
    removedGuard = false
  const cleanups = []
  const originalWindow = globalThis.window
  t.after(() => {
    globalThis.window = originalWindow
  })
  globalThis.window = {
    addEventListener: (_, callback) => {
      beforeUnload = callback
    },
    removeEventListener: (_, callback) => {
      removed = callback
    },
  }
  globalThis.leaveHarness = {
    onMounted: (callback) => callback(),
    onBeforeUnmount: (callback) => {
      cleanups.push(callback)
      cleanup = () => cleanups.forEach((item) => item())
    },
    onBeforeRouteLeave: (callback) => {
      guard = callback
    },
    onBeforeRouteUpdate: (callback) => {
      updateGuard = callback
    },
    useRouter: () => ({
      beforeEach: (callback) => {
        globalGuard = callback
        return () => {
          removedGuard = true
        }
      },
    }),
    ElMessageBox: {
      confirm: async () => {
        prompts++
        if (cancel) throw new Error('cancel')
      },
    },
  }
  const { useLeaveProtection } = await import(path.href)
  let dirty = false,
    busy = false
  useLeaveProtection(
    () => dirty,
    () => busy,
  )
  assert.equal(await guard(), true)
  assert.equal(prompts, 0)
  busy = true
  assert.equal(await guard(), false)
  assert.equal(prompts, 0)
  busy = false
  dirty = true
  assert.equal(await guard(), false)
  cancel = false
  assert.equal(await updateGuard(), true)
  let prevented = false
  beforeUnload({
    preventDefault: () => {
      prevented = true
    },
  })
  assert.equal(prevented, true)
  cleanup()
  assert.equal(removed, beforeUnload)
  useLeaveProtection(
    () => dirty,
    () => busy,
    true,
  )
  cancel = true
  assert.equal(await globalGuard(), false)
  cancel = false
  assert.equal(await globalGuard(), true)
  busy = true
  assert.equal(await globalGuard(), false)
  cleanup()
  assert.equal(removedGuard, true)
})

test('global work-order drawer loads authorized context, keeps the origin and cleans legacy query on close', async (t) => {
  const state = await mount(t, 'WorkOrderDrawer', {}, async (path) =>
    path.startsWith('/assets/')
      ? { id: 'asset', plantId: 'plant', name: 'Pump', code: 'P-101' }
      : { id: 'plant', name: 'North' },
  )
  state.drawer.open({ assetId: 'asset', orderId: 'order' })
  await flush()
  assert.equal(state.asset.id, 'asset')
  assert.equal(state.plant.id, 'plant')
  assert.deepEqual(globalThis.panelHarness.navigation, [])
  await state.created('created-order')
  assert.equal(state.drawer.target.orderId, 'created-order')
  await state.close()
  assert.equal(state.drawer.target, null)
  assert.deepEqual(globalThis.panelHarness.navigation, [])
  globalThis.panelHarness.route.path = '/assets'
  globalThis.panelHarness.route.fullPath = '/assets?workOrderId=order'
  globalThis.panelHarness.route.query = { assetId: 'asset', tab: 'orders', workOrderId: 'order' }
  state.drawer.open({ assetId: 'asset', orderId: 'order' })
  await flush()
  await state.close()
  assert.deepEqual(globalThis.panelHarness.navigation, [
    { path: '/assets', query: { assetId: 'asset', tab: 'orders' } },
  ])
})

test('a delayed work-order asset read cannot replace a newer drawer selection', async (t) => {
  let resolveOld
  const state = await mount(t, 'WorkOrderDrawer', {}, async (path) => {
    if (path === '/assets/old')
      return new Promise((resolve) => {
        resolveOld = resolve
      })
    if (path.startsWith('/assets/')) return { id: 'new', plantId: 'plant' }
    return { id: 'plant' }
  })
  state.drawer.open({ assetId: 'old', orderId: 'old-order' })
  await flush()
  state.drawer.open({ assetId: 'new', orderId: 'new-order' })
  await flush()
  resolveOld({ id: 'old', plantId: 'plant' })
  await flush()
  assert.equal(state.asset.id, 'new')
  assert.equal(state.drawer.target.orderId, 'new-order')
})

test('v3 investigation decisions come from fields while legacy records remain readable', async (t) => {
  const state = await mount(t, 'InvestigationPanel', { alarmId: 'alarm' }, async (path) =>
    path === '/investigation-capabilities' ? { model: 'fixture', externalData: false } : [],
  )
  const advice = {
    schemaVersion: '3',
    status: 'NeedsMoreEvidence',
    statusReason: '现场工况待核查',
    blockingGapIds: ['G1'],
    summary: '已整理诊断计划',
    findings: [],
    hypotheses: [],
    recommendations: [],
    gaps: [],
    workOrder: {
      status: 'DraftReady',
      reason: '可安排诊断工作',
      blockingGapIds: [],
      draft: {
        title: '核查',
        priority: 'High',
        priorityReason: '核查异常',
        objective: '取得依据',
        recommendationIds: ['R1'],
      },
    },
  }
  state.runs = [{ id: 'v3', evidence: [], warnings: [], result: advice }]
  state.selectedId = 'v3'
  assert.equal(state.advice.status, 'NeedsMoreEvidence')
  assert.equal(state.advice.workOrder.status, 'DraftReady')
  assert.equal(state.legacyResult, null)
  state.runs[0].result.summary = '正常、异常、无法确认、禁止写入等文字不决定流程'
  assert.equal(state.advice.status, 'NeedsMoreEvidence')
  assert.equal(state.advice.workOrder.status, 'DraftReady')
  state.runs[0].result = {
    summary: '旧调查结果',
    observations: [],
    nextChecks: [],
    hypotheses: [],
    missingInformation: [],
  }
  assert.equal(state.advice, null)
  assert.equal(state.legacyResult.summary, '旧调查结果')
})

test('the summary uses the matching alarm point and preserves a retained observation with its trust', async (t) => {
  const state = await mount(
    t,
    'InvestigationPanel',
    { alarmId: 'alarm-1', pointCode: 'VIBRATION' },
    async (path) =>
      path === '/investigation-capabilities' ? { model: 'fixture', externalData: false } : [],
  )
  state.runs = [
    {
      id: 'run-1',
      evidence: [
        { id: 'E8', kind: 'EventDerivedTrend', resourceId: 'temperature' },
        {
          id: 'E9',
          kind: 'EventDerivedTrend',
          resourceId: 'vibration',
          derivedTrend: {
            change: { from: '2026-11-14T06:01:23.690722Z', to: '2026-11-14T06:02:00.18771Z' },
          },
        },
      ],
      assessment: {
        anomalyEvidence: 'Insufficient',
        points: [
          {
            pointId: 'temperature',
            code: 'TEMPERATURE',
            currentTrust: 'Trusted',
            observation: { value: 65 },
          },
          {
            pointId: 'vibration',
            code: 'VIBRATION',
            currentTrust: 'Trusted',
            observation: { value: 0, effectiveAt: '2026-11-14T06:02:00.18771Z', quality: 'Good' },
          },
        ],
      },
    },
  ]
  state.selectedId = 'run-1'
  assert.equal(state.observedAlarmPoint.observation.value, 0)
  assert.match(state.overview, /证据不足/)
  state.runs[0].assessment.points[1].currentTrust = 'Unverifiable'
  assert.equal(state.observedAlarmPoint.currentTrust, 'Unverifiable')
  assert.equal(state.observedAlarmPoint.observation.effectiveAt, '2026-11-14T06:02:00.18771Z')
  assert.equal(state.alarmEventTrend.id, 'E9')
  state.runs[0].assessment.points[1].observation = null
  assert.equal(state.observedAlarmPoint, undefined)
  state.runs[0].assessment = null
  assert.equal(state.observedAlarmPoint, undefined)
})

test('the next step uses a unique explicit blocker link and never defaults to the first task', async (t) => {
  const state = await mount(t, 'InvestigationPanel', { alarmId: 'alarm-1' }, async (path) =>
    path === '/investigation-capabilities' ? { model: 'fixture', externalData: false } : [],
  )
  state.runs = [
    {
      id: 'run-1',
      result: {
        schemaVersion: '3',
        recommendations: [{ id: 'R1' }, { id: 'R2' }],
        blockingGapIds: ['G2'],
        gaps: [
          { id: 'G1', resolutionRecommendationIds: ['R1'] },
          { id: 'G2', resolutionRecommendationIds: ['R2'] },
        ],
      },
      evidence: [
        { id: 'E1', kind: 'Alarm' },
        { id: 'E2', kind: 'FutureSource' },
        { id: 'E3', kind: 'Document' },
        { id: 'E4', kind: 'TriggerReading' },
      ],
    },
  ]
  state.selectedId = 'run-1'
  assert.equal(state.nextRecommendation.id, 'R2')
  state.runs[0].result.blockingGapIds.push('G1')
  assert.equal(state.nextRecommendation, undefined)
  state.runs[0].result.blockingGapIds = []
  assert.equal(state.nextRecommendation, undefined)
  state.runs[0].result.recommendations = [{ id: 'R2' }]
  assert.equal(state.nextRecommendation.id, 'R2')
  assert.deepEqual(
    state.evidenceGroups.map((group) => group.items.map(({ id }) => id)),
    [['E1', 'E4'], ['E3'], ['E2']],
  )
})

test('a late evidence response cannot replace the newer citation or reappear after a run change', async (t) => {
  const requests = new Map()
  const state = await mount(t, 'InvestigationPanel', { alarmId: 'alarm-1' }, async (path) => {
    if (path === '/investigation-capabilities') return { model: 'fixture', externalData: false }
    if (path.endsWith('/investigations')) return []
    return new Promise((resolve) => requests.set(path.split('/').at(-1), resolve))
  })
  state.runs = [{ id: 'run-1' }, { id: 'run-2' }]
  state.selectedId = 'run-1'
  const older = state.expand('E1')
  const newer = state.expand('E2')
  requests.get('E2')({ id: 'E2' })
  await newer
  requests.get('E1')({ id: 'E1' })
  await older
  assert.equal(state.expanded.id, 'E2')
  const previousRun = state.expand('E3')
  state.selectedId = 'run-2'
  state.choose()
  requests.get('E3')({ id: 'E3' })
  await previousRun
  assert.equal(state.expanded, null)
})

test('the advice view renders distinct tasks once and preserves explicit links to checks and evidence', async () => {
  const check = {
    kind: 'Inspect',
    subjectRefs: ['P-101'],
    evidenceRefs: ['E1'],
    record: 'Record operating load.',
    completionCriteria: 'Readings recorded.',
    escalation: { condition: 'Vibration persists.', responsibleRole: 'MaintenanceEngineer' },
  }
  const app = createSSRApp(components.InvestigationAdvicePanel, {
    recommendationPrefix: 'check-',
    taskListId: 'task-list',
    taskKinds: { Inspect: '现场核查' },
    subjects: [],
    advice: {
      blockingGapIds: ['G1'],
      recommendations: [
        { ...check, id: 'R1', task: 'Inspect the drive-end bearing.' },
        { ...check, id: 'R2', task: 'Inspect the non-drive-end bearing.' },
      ],
      hypotheses: [
        {
          id: 'H1',
          description: 'Load variation may contribute.',
          support: 'Weak',
          supportingEvidenceRefs: ['E1'],
          contradictingEvidenceRefs: [],
          verificationRecommendationIds: ['R1'],
        },
      ],
      gaps: [
        {
          id: 'G1',
          kind: 'RequiresOnSite',
          description: 'Operating load is unknown.',
          whyItMatters: 'Compare equivalent operating conditions.',
          evidenceRefs: ['E1'],
          resolutionRecommendationIds: ['R2'],
        },
      ],
      findings: [
        {
          id: 'F1',
          scope: 'AlarmEvent',
          domain: 'MeasurementChain',
          status: 'AnomalySupported',
          subjectRefs: ['P-101'],
          evidenceRefs: ['E1'],
          interpretation:
            '历史观测段为 2026-11-14T06:01:23.690722Z 至 2026-11-14T06:02:00.18771Z。',
        },
      ],
      workOrder: { blockingGapIds: [], draft: { recommendationIds: ['R2'] } },
    },
  })
  app.component('el-button', (_, { attrs, slots }) =>
    h('button', { 'aria-label': attrs['aria-label'] }, slots.default?.()),
  )
  const html = await renderToString(app)
  assert.equal(html.split('Inspect the drive-end bearing.').length, 2)
  assert.equal(html.split('Inspect the non-drive-end bearing.').length, 2)
  assert.match(
    html,
    /<details id="check-R1"[^>]*><summary>.*Inspect the drive-end bearing.*<\/summary>/,
  )
  assert.match(
    html,
    /<details id="check-R2"[^>]*><summary>.*纳入工单建议.*Inspect the non-drive-end bearing.*<\/summary>/,
  )
  assert.equal(html.split('纳入工单建议').length, 2)
  assert.match(html, /Record operating load/)
  assert.match(html, /Readings recorded/)
  assert.match(html, /Vibration persists/)
  assert.match(
    html,
    /历史观测段为 2026-11-14 14:01:23\.690722（北京时间） 至 2026-11-14 14:02:00\.18771（北京时间）/,
  )
  assert.match(html, /核实方式：.*aria-label="打开核查任务 R1">R1<\/button>/)
  assert.match(html, /补证任务.*aria-label="打开核查任务 R2">R2<\/button>/)
  assert.match(html, /<button aria-label="查看证据 E1">E1<\/button>/)
  assert.match(html, /<details[^>]* open[^>]*><summary>仍缺少的信息/)
})

test('v3 draft gating blocks new work orders without disabling investigation review', async (t) => {
  const calls = []
  const state = await mount(
    t,
    'ActionProposalPanel',
    { runId: 'run', available: true, canCreateWorkOrder: false },
    async (path, method) => {
      calls.push({ path, method })
      return []
    },
  )
  await state.create()
  assert.equal(calls.filter((x) => x.method === 'POST').length, 0)
  assert.equal(state.proposal, null)
})
