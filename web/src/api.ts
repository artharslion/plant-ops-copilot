import type { components } from './generated/api'
export type DirectoryRecord = components['schemas']['DirectoryRecord']
export type DirectoryPage = components['schemas']['DirectoryPage']
export type CurrentUser = components['schemas']['CurrentUser']
export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message)
  }
}
export async function request<T>(path: string, method = 'GET', body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (method !== 'GET') {
    const csrf = await request<{ token: string }>('/auth/csrf')
    headers['X-CSRF-TOKEN'] = csrf.token
    if (body !== undefined && !(body instanceof FormData))
      headers['Content-Type'] = 'application/json'
  }
  let response: Response
  try {
    response = await fetch(`/api/v1${path}`, {
      method,
      headers,
      credentials: 'same-origin',
      cache: 'no-store',
      signal: AbortSignal.timeout(10000),
      body: body === undefined ? undefined : body instanceof FormData ? body : JSON.stringify(body),
    })
  } catch {
    throw new ApiError(0, '无法连接服务，请检查连接后重试。')
  }
  if (!response.ok) {
    const problem = (await response.json().catch(() => ({}))) as {
      title?: string
      correlationId?: string
    }
    const messages: Record<number, string> = {
      401: '会话已失效或登录信息不正确，请重新登录。',
      403: '当前角色无权执行此操作。',
      404: '资源不存在或不在你的工厂授权范围内。',
      409: '此记录已被修改。请关闭编辑、刷新并重新检查后再保存。',
      500: '服务暂时不可用，请稍后重试。',
      502: '服务暂时不可用，请稍后重试。',
      503: '服务暂时不可用，请稍后重试。',
    }
    throw new ApiError(
      response.status,
      messages[response.status] ?? problem.title ?? `请求失败 (${response.status})`,
    )
  }
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T)
}
export async function downloadDocument(path: string, fileName: string) {
  let response: Response
  try {
    response = await fetch(`/api/v1${path}`, {
      credentials: 'same-origin',
      cache: 'no-store',
      signal: AbortSignal.timeout(10000),
    })
  } catch {
    throw new ApiError(0, '无法连接服务，请重试。')
  }
  if (!response.ok)
    throw new ApiError(
      response.status,
      response.status === 404
        ? '文档未发布、已撤回或不在授权范围内。'
        : '文档下载失败，请刷新并检查权限。',
    )
  const url = URL.createObjectURL(await response.blob()),
    link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.click()
  setTimeout(() => URL.revokeObjectURL(url), 30000)
}
export async function listAll(path: string): Promise<DirectoryRecord[]> {
  const items: DirectoryRecord[] = []
  let cursor: string | null = null
  do {
    const page: DirectoryPage = await request(
      `${path}${path.includes('?') ? '&' : '?'}limit=200${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`,
    )
    items.push(...page.items)
    cursor = page.nextCursor
  } while (cursor)
  return items
}
