export interface DirectoryRecord {
  id: string; plantId: string; code: string; name: string; version: number; isArchived: boolean
  description: string | null; parentLocationId: string | null; sortOrder: number
  displayTimeZone: string; locationId: string | null; assetType: string
  lifecycleStatus: string; criticality: string; manufacturer: string | null
  model: string | null; serialNumber: string | null; installedAt: string | null
}
export interface DirectoryPage { items: DirectoryRecord[]; nextCursor: string | null }
export interface CurrentUser { id: string; userName: string; displayName: string; roles: string[] }
export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}
export async function request<T>(path: string, method = 'GET', body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (method !== 'GET') {
    const csrf = await request<{ token: string }>('/auth/csrf')
    headers['X-CSRF-TOKEN'] = csrf.token
    if (body !== undefined) headers['Content-Type'] = 'application/json'
  }
  let response: Response
  try {
    response = await fetch(`/api/v1${path}`, { method, headers, credentials: 'same-origin', cache: 'no-store',
      signal: AbortSignal.timeout(10000), body: body === undefined ? undefined : JSON.stringify(body) })
  } catch { throw new ApiError(0, '无法连接服务，请检查连接后重试。') }
  if (!response.ok) {
    const problem = await response.json().catch(() => ({})) as { title?: string; correlationId?: string }
    const messages: Record<number, string> = {
      401: '会话已失效或登录信息不正确，请重新登录。', 403: '当前角色无权执行此操作。',
      404: '资源不存在或不在你的工厂授权范围内。', 409: '此记录已被修改。请关闭编辑、刷新并重新检查后再保存。',
      500: '服务暂时不可用，请稍后重试。', 502: '服务暂时不可用，请稍后重试。', 503: '服务暂时不可用，请稍后重试。',
    }
    throw new ApiError(response.status, messages[response.status] ?? problem.title ?? `请求失败 (${response.status})`)
  }
  return response.status === 204 ? undefined as T : await response.json() as T
}
export async function listAll(path: string): Promise<DirectoryRecord[]> {
  const items: DirectoryRecord[] = []
  let cursor: string | null = null
  do {
    const page: DirectoryPage = await request(`${path}${path.includes('?') ? '&' : '?'}limit=200${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`)
    items.push(...page.items); cursor = page.nextCursor
  } while (cursor)
  return items
}
