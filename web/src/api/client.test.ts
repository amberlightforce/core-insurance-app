import { afterEach, describe, expect, it, vi } from 'vitest';

import { mockApi, problem } from '../test/mockApi';
import { ApiError, apiRequest, isApiError } from './client';
import { newIdempotencyKey } from './idempotency';

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('apiRequest', () => {
  it('sends JSON bodies, the Idempotency-Key and Accept-Language, and puts query values in the URL', async () => {
    const api = mockApi([
      {
        method: 'POST',
        path: '/api/x/v1/things',
        respond: () => ({ status: 201, body: { ok: true } }),
      },
    ]);
    const key = newIdempotencyKey();
    const result = await apiRequest<{ ok: boolean }>('/api/x/v1/things', {
      method: 'POST',
      body: { a: 1 },
      query: { limit: 25, skip: undefined, empty: '', flag: false },
      idempotencyKey: key,
    });
    expect(result).toEqual({ ok: true });
    const call = api.calls[0];
    expect(call?.url.search).toBe('?limit=25&flag=false');
    expect(call?.body).toEqual({ a: 1 });
    expect(call?.headers.get('Idempotency-Key')).toBe(key);
    expect(call?.headers.get('Content-Type')).toBe('application/json');
    expect(call?.headers.get('Accept-Language')).toBe('el');
  });

  it('sends the dev bearer token when a session is held', async () => {
    sessionStorage.setItem(
      'coreins.devSession',
      JSON.stringify({
        accessToken: 'tok-1',
        expiresAt: new Date(Date.now() + 60_000).toISOString(),
        user: { id: 'u', name: 'U', roles: [] },
      }),
    );
    const api = mockApi([{ method: 'GET', path: '/api/x/v1/a', respond: () => ({ body: {} }) }]);
    await apiRequest('/api/x/v1/a');
    expect(api.calls[0]?.headers.get('Authorization')).toBe('Bearer tok-1');
  });

  it('turns an RFC 9457 problem into an ApiError carrying code, status and trace id', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/x/v1/a',
        respond: () => problem(422, 'POL-ERR-VALIDATION', 'Invalid', 'Add a vehicle.'),
      },
    ]);
    const error = await apiRequest('/api/x/v1/a').catch((e: unknown) => e);
    expect(isApiError(error)).toBe(true);
    const api = error as ApiError;
    expect(api.status).toBe(422);
    expect(api.code).toBe('POL-ERR-VALIDATION');
    expect(api.problem.traceId).toBe('trace-0123456789abcdef');
    expect(api.message).toBe('Add a vehicle.');
  });

  it('synthesises a problem for non-JSON failures and for network errors', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response('<html>bad gateway</html>', { status: 502 }))),
    );
    const gateway = (await apiRequest('/api/x').catch((e: unknown) => e)) as ApiError;
    expect(gateway.status).toBe(502);
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.reject(new TypeError('Failed to fetch'))),
    );
    const offline = (await apiRequest('/api/x').catch((e: unknown) => e)) as ApiError;
    expect(offline.status).toBe(0);
    expect(offline.code).toBe('NETWORK');
    expect(offline.problem.retryable).toBe(true);
  });
});

describe('401 handling', () => {
  it('drops the held dev session and runs the registered handler', async () => {
    const { onUnauthorized } = await import('./client');
    sessionStorage.setItem(
      'coreins.devSession',
      JSON.stringify({
        accessToken: 'stale',
        expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
        user: { id: 'u', name: 'U', roles: [] },
      }),
    );
    mockApi([
      {
        method: 'GET',
        path: '/api/x/v1/things',
        respond: () => problem(401, 'PLT-ERR-UNAUTHENTICATED', 'Unauthorized'),
      },
    ]);
    const handler = vi.fn();
    onUnauthorized(handler);
    await expect(apiRequest('/api/x/v1/things')).rejects.toSatisfy(isApiError);
    expect(handler).toHaveBeenCalledOnce();
    expect(sessionStorage.getItem('coreins.devSession')).toBeNull();
    onUnauthorized(null);
  });
});
