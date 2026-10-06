import { ref } from 'vue'
import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
export type PlantEvent = {
  eventId: string
  plantId: string
  entityId: string
  entityVersion: number
  eventType: string
  occurredAt: string
}
export const realtimeStatus = ref('未连接')
let connection: HubConnection | undefined
let restart: ReturnType<typeof setTimeout> | undefined
let active = false
const listeners = new Set<(event: PlantEvent | null) => void>()
const seen = new Set<string>(),
  versions = new Map<string, number>()
export function subscribePlantEvents(callback: (event: PlantEvent | null) => void) {
  listeners.add(callback)
  return () => {
    listeners.delete(callback)
  }
}
function notify(event: PlantEvent | null) {
  for (const callback of listeners) callback(event)
}
export function acceptPlantEvent(event: PlantEvent) {
  if (seen.has(event.eventId)) return false
  seen.add(event.eventId)
  if (seen.size > 2000) seen.delete(seen.values().next().value!)
  const key = `${event.eventType}:${event.entityId}`
  if (event.eventType !== 'DocumentsChanged') {
    if ((versions.get(key) ?? 0) >= event.entityVersion) return false
    versions.set(key, event.entityVersion)
    if (versions.size > 2000) versions.delete(versions.keys().next().value!)
  }
  notify(event)
  return true
}
async function connect() {
  if (!active || !connection) return
  try {
    await connection.start()
    realtimeStatus.value = '实时已连接'
    seen.clear()
    versions.clear()
    notify(null)
  } catch {
    realtimeStatus.value = '实时离线 · 定期补查'
    if (active) restart = setTimeout(connect, 5000)
  }
}
export function startRealtime() {
  if (active) return
  active = true
  connection = new HubConnectionBuilder()
    .withUrl('/hubs/plant-events')
    .withAutomaticReconnect([0, 1000, 3000, 5000])
    .configureLogging(LogLevel.None)
    .build()
  connection.on('PlantEvent', acceptPlantEvent)
  connection.onreconnecting(() => {
    realtimeStatus.value = '正在重连 · 定期补查'
  })
  connection.onreconnected(() => {
    realtimeStatus.value = '实时已连接'
    seen.clear()
    versions.clear()
    notify(null)
  })
  connection.onclose(() => {
    realtimeStatus.value = '实时离线 · 定期补查'
    if (active) restart = setTimeout(connect, 5000)
  })
  void connect()
}
export async function stopRealtime() {
  active = false
  clearTimeout(restart)
  await connection?.stop()
  connection = undefined
  seen.clear()
  versions.clear()
  realtimeStatus.value = '未连接'
  notify(null)
}
