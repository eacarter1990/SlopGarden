export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  let res: Response
  try {
    res = await fetch(url, {
      method,
      credentials: 'same-origin',
      headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    })
  } catch (err) {
    if (err instanceof TypeError) {
      throw new ApiError(0, 'The server did not answer. Open SlopGarden at http://127.0.0.1:5080 and check that it is running.')
    }
    throw err
  }
  const text = await res.text()
  const data = text ? safeJson(text) : null
  if (!res.ok) {
    const message = (data && typeof data === 'object' && 'error' in data ? String((data as { error: unknown }).error) : null) ?? `${res.status} ${res.statusText}`
    throw new ApiError(res.status, message)
  }
  return data as T
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return text
  }
}

export const api = {
  get: <T>(url: string) => request<T>('GET', url),
  post: <T>(url: string, body?: unknown) => request<T>('POST', url, body ?? {}),
  put: <T>(url: string, body?: unknown) => request<T>('PUT', url, body ?? {}),
  del: <T>(url: string) => request<T>('DELETE', url),
  /** Run a tool from the shared registry (the same operations the AI and MCP clients use). */
  op: <T>(tool: string, args: Record<string, unknown> = {}) => request<T>('POST', `/api/ops/${tool}`, args),
}
