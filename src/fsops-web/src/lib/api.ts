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
    }
  } catch {
    // response body wasn't JSON (or was empty) — fall back below
  }
  return response.statusText || `Request failed with status ${response.status}`
}

/**
 * Minimal typed fetch wrapper for the FSOps API. Every failure — network
 * errors, non-2xx responses, unparsable bodies — normalises into a thrown
 * ApiError so callers can branch on `.status` without worrying about fetch
 * quirks. AbortError is rethrown as-is so callers can distinguish a
 * cancelled request (e.g. a superseded debounced search) from a real
 * failure.
 */
export async function get<T>(path: string, params?: QueryParams, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await fetch(buildPath(path, params), {
      ...init,
      headers: { Accept: 'application/json', ...init?.headers },
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
