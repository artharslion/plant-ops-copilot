// Local HTTP/SignalR acceptance load, not a browser automation tool. No credentials are written to reports.
import fs from 'node:fs/promises'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
const base = process.env.PLANTOPS_BENCH_URL ?? 'http://127.0.0.1:5080'
const minutes = Number(process.env.PLANTOPS_BENCH_MINUTES ?? 30)
if (!(minutes >= 1 && minutes <= 60) || !process.env.Demo__Password) throw new Error('Set local demo password and a duration from 1 to 60 minutes.')
const directory = new URL('../../artifacts/p17/', import.meta.url); await fs.mkdir(directory,{recursive:true})
const percentile = values => { const sorted = [...values].sort((a,b)=>a-b); return {count:sorted.length,p50:sorted[Math.floor(sorted.length*0.5)] ?? null,p95:sorted[Math.min(sorted.length-1,Math.floor(sorted.length*0.95))] ?? null,max:sorted.at(-1) ?? null} }
const clients = []
for (const name of ['viewer','operator','engineer','admin','admin']) {
  const jar = new Map(), times = {list:[],overview:[],trend:[],pushAndSnapshot:[]}, failures = []
  const cookie = () => [...jar].map(([k,v])=>`${k}=${v}`).join('; ')
  async function call(path, method='GET', body, token) {
    const response = await fetch(base + '/api/v1' + path,{method,headers:{Cookie:cookie(),...(body ? {'Content-Type':'application/json'}:{}),...(token ? {'X-CSRF-TOKEN':token}:{})},body:body ? JSON.stringify(body):undefined,signal:AbortSignal.timeout(10000)})
    for (const c of response.headers.getSetCookie()) { const first=c.split(';')[0],at=first.indexOf('='); jar.set(first.slice(0,at),first.slice(at+1)) }
    if (!response.ok) throw new Error(`HTTP ${response.status} at ${path.split('?')[0]}`)
    return response.status===204 ? undefined : response.json()
  }
  const csrf = await call('/auth/csrf'); await call('/auth/login','POST',{userName:name,password:process.env.Demo__Password},csrf.token)
  const grants = (await call('/plants?limit=200')).items.map(x=>x.id)
  const plant = name==='engineer' ? '10000000-0000-0000-0000-000000000002':'10000000-0000-0000-0000-000000000001'
  const asset = name==='engineer' ? '30000000-0000-0000-0000-000000000002':'30000000-0000-0000-0000-000000000001'
  const points=await call(`/assets/${asset}/measurement-points`); const point=points.find(x=>x.code==='VIBRATION_RMS').id
  const connection = new HubConnectionBuilder().withUrl(base+'/hubs/plant-events',{headers:{Cookie:cookie()}}).withAutomaticReconnect().configureLogging(LogLevel.None).build()
  let fetching = false, received = 0, duplicates = 0, unauthorized = 0, reconnects = 0; const ids = new Set()
  connection.on('PlantEvent', async event => {
    received++; if (!grants.includes(event.plantId)) { unauthorized++; return }
    if (ids.has(event.eventId)) { duplicates++; return }; ids.add(event.eventId); if(ids.size>20000) ids.delete(ids.values().next().value)
    if (event.eventType!=='MeasurementChanged' || event.entityId!==point || fetching) return
    fetching=true
    try { await call(`/assets/${asset}/measurement-points`); times.pushAndSnapshot.push(Date.now()-Date.parse(event.occurredAt)) }
    catch(e) { failures.push(e.message) } finally { fetching=false }
  })
  connection.onreconnected(()=>reconnects++)
  await connection.start()
  clients.push({name,times,failures,call,plant,asset,point,connection,stats:()=>({received,duplicates,unauthorized,reconnects})})
}
const startedAt = new Date(), deadline = Date.now()+minutes*60000
await fs.writeFile(new URL('benchmark-progress.json',directory),JSON.stringify({startedAt,durationMinutes:minutes,status:'running'}))
console.log(`Five authorized clients connected. ${minutes}-minute wall-clock run started ${startedAt.toISOString()}.`)
async function sample(client,path,category) { const at=performance.now(); try { await client.call(path); client.times[category].push(performance.now()-at) } catch(e) {client.failures.push(e.message)} }
let cycles=0
while(Date.now()<deadline) {
  await Promise.all(clients.map(async c => {
    await sample(c,`/assets?plantId=${c.plant}&limit=50`,'list')
    await sample(c,`/assets/${c.asset}/measurement-points`,'overview')
    const to=new Date(Math.floor(Date.now()/60000)*60000),from=new Date(to.getTime()-86400000)
    await sample(c,`/assets/${c.asset}/measurements?pointId=${c.point}&from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}&bucket=1m`,'trend')
  }))
  cycles++
  if(cycles%12===0) { const progress={startedAt,durationMinutes:minutes,status:'running',elapsedSeconds:Math.round((Date.now()-startedAt.getTime())/1000),clients:clients.map(c=>({name:c.name,...c.stats(),failures:c.failures.length,timing:Object.fromEntries(Object.entries(c.times).map(([k,v])=>[k,percentile(v)]))}))}; await fs.writeFile(new URL('benchmark-progress.json',directory),JSON.stringify(progress,null,2)); console.log(`Acceptance load: ${progress.elapsedSeconds}s elapsed, ${clients.reduce((n,c)=>n+c.failures.length,0)} request failures.`) }
  await new Promise(resolve=>setTimeout(resolve,Math.min(5000,Math.max(0,deadline-Date.now()))))
}
await Promise.all(clients.map(c=>c.connection.stop()))
const endedAt=new Date(),report={startedAt,endedAt,elapsedSeconds:(endedAt-startedAt)/1000,durationMinutes:minutes,clients:clients.map(c=>({name:c.name,...c.stats(),failures:c.failures,timing:Object.fromEntries(Object.entries(c.times).map(([k,v])=>[k,percentile(v)]))}))}
report.passed=report.clients.every(c=>c.failures.length===0 && c.unauthorized===0 && c.timing.list.p95<500 && c.timing.overview.p95<500 && c.timing.pushAndSnapshot.p95<2000)
await fs.writeFile(new URL('benchmark-results.json',directory),JSON.stringify(report,null,2)); await fs.writeFile(new URL('benchmark-progress.json',directory),JSON.stringify({...report,status:'completed'},null,2))
console.log(`Benchmark complete. passed=${report.passed}. Detailed report: artifacts/p17/benchmark-results.json`)
if(!report.passed)process.exitCode=1
