import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import ts from 'typescript'
const cache = new URL('../node_modules/.cache/', import.meta.url)
await fs.mkdir(cache, {recursive:true})
const output = new URL('plantops-realtime-test.mjs', cache)
await fs.writeFile(output, ts.transpileModule(await fs.readFile(new URL('../src/realtime.ts', import.meta.url),'utf8'), {compilerOptions:{module:ts.ModuleKind.ESNext,target:ts.ScriptTarget.ES2022}}).outputText)
const realtime = await import(output.href)
test('duplicate IDs and stale versions never notify twice; document changes remain distinct', () => {
  const events = []; const unsubscribe = realtime.subscribePlantEvents(e => events.push(e))
  const e = {eventId:'one',plantId:'north',entityId:'pump',entityVersion:2,eventType:'WorkOrderChanged',occurredAt:'2026-10-05T00:00:00Z'}
  assert.equal(realtime.acceptPlantEvent(e),true)
  assert.equal(realtime.acceptPlantEvent(e),false)
  assert.equal(realtime.acceptPlantEvent({...e,eventId:'retry',entityVersion:1}),false)
  assert.equal(realtime.acceptPlantEvent({...e,eventId:'new',entityVersion:3}),true)
  assert.equal(realtime.acceptPlantEvent({...e,eventId:'doc-one',entityId:'',eventType:'DocumentsChanged'}),true)
  assert.equal(realtime.acceptPlantEvent({...e,eventId:'doc-two',entityId:'',eventType:'DocumentsChanged'}),true)
  assert.equal(events.length,4); unsubscribe()
})
