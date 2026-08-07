const API_BASE = '/api/v1'

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

type QueryValue = string | number | boolean | undefined | null
type QueryParams = Record<string, QueryValue>

function buildPath(path: string, params?: QueryParams): string {
  const search = new URLSearchParams()
  if (params) {
    for (const [key, value] of Object.entries(params)) {
      if (value !== undefined && value !== null) {
        search.set(key, String(value))
      }
    }
  }
  const query = search.toString()
  return `${API_BASE}${path}${query ? `?${query}` : ''}`
}

async function extractErrorMessage(response: Response): Promise<string> {
  try {
    const body: unknown = await response.clone().json()
    if (body && typeof body === 'object') {
      const record = body as Record<string, unknown>
      if (typeof record.message === 'string') return record.message
      if (typeof record.title === 'string') return record.title
      if (typeof record.error === 'string') return record.error
    }
  } catch {
    // response body wasn't JSON (or was empty) — fall back below
  }
  return response.statusText || `Request failed with status ${response.status}`
}

interface RequestOptions {
  params?: QueryParams
  body?: unknown
  init?: RequestInit
}

/**
 * Shared request core. Every failure — network errors, non-2xx responses,
 * unparsable bodies — normalises into a thrown ApiError so callers can
 * branch on `.status` without worrying about fetch quirks. AbortError is
 * rethrown as-is so callers can distinguish a cancelled request (e.g. a
 * superseded debounced search) from a real failure.
 */
async function request<T>(method: string, path: string, options?: RequestOptions): Promise<T> {
  let response: Response
  try {
    response = await fetch(buildPath(path, options?.params), {
      ...options?.init,
      method,
      headers: {
        Accept: 'application/json',
        ...(options?.body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        ...options?.init?.headers,
      },
      body: options?.body !== undefined ? JSON.stringify(options.body) : undefined,
    })
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') throw cause
    throw new ApiError(0, 'Could not reach the server. Check your connection and try again.')
  }

  if (!response.ok) {
    throw new ApiError(response.status, await extractErrorMessage(response))
  }

  if (response.status === 204) {
    return undefined as T
  }

  return (await response.json()) as T
}

export function get<T>(path: string, params?: QueryParams, init?: RequestInit): Promise<T> {
  return request<T>('GET', path, { params, init })
}

export function post<T = unknown>(path: string, body?: unknown, params?: QueryParams, init?: RequestInit): Promise<T> {
  return request<T>('POST', path, { body, params, init })
}

export function put<T = unknown>(path: string, body?: unknown, params?: QueryParams, init?: RequestInit): Promise<T> {
  return request<T>('PUT', path, { body, params, init })
}

export function del<T = unknown>(path: string, params?: QueryParams, init?: RequestInit): Promise<T> {
  return request<T>('DELETE', path, { params, init })
}
