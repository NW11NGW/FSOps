const API_BASE = '/api/v1'

export class ApiError extends Error {
  readonly status: number
  /** The parsed JSON error body, when the response had one - lets a caller branch on a specific
   *  field (e.g. a stale-quote endpoint returning `currentTotalPayoff`) rather than pattern-matching
   *  on `.message` text, which is for display and can be reworded without warning. Undefined when
   *  the body wasn't JSON or wasn't an object. */
  readonly body: unknown

  constructor(status: number, message: string, body?: unknown) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.body = body
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

interface ParsedErrorResponse {
  message: string
  body: unknown
}

async function extractErrorResponse(response: Response): Promise<ParsedErrorResponse> {
  try {
    const body: unknown = await response.clone().json()
    if (body && typeof body === 'object') {
      const record = body as Record<string, unknown>
      if (typeof record.message === 'string') return { message: record.message, body }
      if (typeof record.title === 'string') return { message: record.title, body }
      if (typeof record.error === 'string') return { message: record.error, body }
    }
    return { message: response.statusText || `Request failed with status ${response.status}`, body }
  } catch {
    // response body wasn't JSON (or was empty) — fall back below
  }
  return { message: response.statusText || `Request failed with status ${response.status}`, body: undefined }
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
    const { message, body } = await extractErrorResponse(response)
    throw new ApiError(response.status, message, body)
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
