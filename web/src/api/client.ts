import { apiFetch, clearSession } from '../dev-auth/devAuth';
import i18n from '../i18n';

/** RFC 9457 Problem Details as the platform returns them (`code`, `traceId`, `errors[]` are platform extensions). */
export interface ProblemFieldError {
  field: string;
  code: string;
  messageKey?: string;
  message?: string | null;
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status: number;
  detail?: string;
  instance?: string;
  code?: string;
  traceId?: string;
  retryable?: boolean;
  errors?: ProblemFieldError[];
  [extension: string]: unknown;
}

/** A failed API call: the Problem Details body (or a synthesised one for network and non-JSON failures). */
export class ApiError extends Error {
  readonly problem: ProblemDetails;

  constructor(problem: ProblemDetails) {
    super(problem.detail ?? problem.title ?? `HTTP ${String(problem.status)}`);
    this.name = 'ApiError';
    this.problem = problem;
  }

  get status(): number {
    return this.problem.status;
  }

  get code(): string | undefined {
    return this.problem.code;
  }
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError;
}

let unauthorizedHandler: (() => void) | null = null;

/**
 * Registers what happens when the API answers 401 (a dev token that no longer verifies, for instance after the api
 * restarted with a new signing key): the held session is dropped first, then the handler runs (the app sends the user
 * to the sign-in page).
 */
export function onUnauthorized(handler: (() => void) | null): void {
  unauthorizedHandler = handler;
}

type QueryValue = string | number | boolean | null | undefined;

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PATCH';
  /** JSON body. Personal data (search terms, identifiers) travels here, never in the path or query (D-SLC-05). */
  body?: unknown;
  /** Non-personal query parameters only. */
  query?: Record<string, QueryValue>;
  /** Commands send an Idempotency-Key: one UUID per user action, reused on retry. */
  idempotencyKey?: string;
  signal?: AbortSignal;
}

function withQuery(path: string, query: Record<string, QueryValue> | undefined): string {
  if (!query) return path;
  const parts = Object.entries(query)
    .filter((entry): entry is [string, string | number | boolean] => {
      const value = entry[1];
      return value !== undefined && value !== null && value !== '';
    })
    .map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`);
  return parts.length ? `${path}?${parts.join('&')}` : path;
}

async function readProblem(response: Response): Promise<ProblemDetails> {
  try {
    const body = (await response.json()) as Partial<ProblemDetails> | null;
    if (body && typeof body === 'object')
      return { ...body, status: body.status ?? response.status };
  } catch {
    // Not JSON (a proxy page, an empty body): fall through to the synthesised problem.
  }
  return { status: response.status, title: response.statusText || undefined } as ProblemDetails;
}

/** Calls the API with the dev bearer token and returns the parsed JSON body; throws {@link ApiError} otherwise. */
export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, query, idempotencyKey, signal } = options;
  const headers = new Headers({ Accept: 'application/json', 'Accept-Language': i18n.language });
  if (body !== undefined) headers.set('Content-Type', 'application/json');
  if (idempotencyKey) headers.set('Idempotency-Key', idempotencyKey);
  let response: Response;
  try {
    response = await apiFetch(withQuery(path, query), {
      method,
      headers,
      ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
      signal: signal ?? null,
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error;
    throw new ApiError({ status: 0, code: 'NETWORK', title: 'Network error', retryable: true });
  }
  if (response.status === 401) {
    clearSession();
    unauthorizedHandler?.();
  }
  if (!response.ok) throw new ApiError(await readProblem(response));
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}
