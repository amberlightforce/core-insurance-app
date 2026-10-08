import { useMemo, useSyncExternalStore } from 'react';

/**
 * Development-only local sign-in (D-SLC-03). The api offers `GET /api/plt/v1/dev/users` and
 * `POST /api/plt/v1/dev/sign-in` only when it runs in Development with `DevAuthentication:Enabled`; anywhere else the
 * endpoints do not exist and production sign-in is Entra ID (MSAL, W1-PLT). The token lives in sessionStorage for
 * the tab only and is sent as a bearer token by {@link apiFetch}.
 */
export interface DevUser {
  id: string;
  name: string;
  roles: string[];
}

export interface DevSession {
  accessToken: string;
  expiresAt: string;
  user: DevUser;
}

const storageKey = 'coreins.devSession';

export function readSession(): DevSession | null {
  try {
    const raw = sessionStorage.getItem(storageKey);
    if (!raw) return null;
    const session = JSON.parse(raw) as DevSession;
    return new Date(session.expiresAt).getTime() > Date.now() ? session : null;
  } catch {
    return null;
  }
}

/** Same-tab change signal: `storage` events only reach other tabs. */
const sessionEvent = 'coreins:session';

function notifySessionChange(): void {
  window.dispatchEvent(new Event(sessionEvent));
}

export function clearSession(): void {
  sessionStorage.removeItem(storageKey);
  notifySessionChange();
}

function subscribeSession(onChange: () => void): () => void {
  window.addEventListener(sessionEvent, onChange);
  window.addEventListener('storage', onChange);
  return () => {
    window.removeEventListener(sessionEvent, onChange);
    window.removeEventListener('storage', onChange);
  };
}

function rawSession(): string | null {
  try {
    return sessionStorage.getItem(storageKey);
  } catch {
    return null;
  }
}

/**
 * The signed-in dev user, re-rendering on sign-in and sign-out. The snapshot is the raw stored string (stable
 * between reads), parsed once per change, so the store never reports a new object on every render.
 */
export function useDevSession(): DevSession | null {
  const raw = useSyncExternalStore(subscribeSession, rawSession, () => null);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- `raw` is the cache key; readSession re-reads it
  return useMemo(() => readSession(), [raw]);
}

/** `unavailable`: the api does not offer dev sign-in (not Development, or the flag is off). */
export type UsersResult = { kind: 'ok'; users: DevUser[] } | { kind: 'unavailable' };

export async function fetchDevUsers(signal?: AbortSignal): Promise<UsersResult> {
  const response = await fetch('/api/plt/v1/dev/users', { signal: signal ?? null });
  if (response.status === 401 || response.status === 404) return { kind: 'unavailable' };
  if (!response.ok) throw new Error(`HTTP ${String(response.status)}`);
  const body = (await response.json()) as { items: DevUser[] };
  return { kind: 'ok', users: body.items };
}

export async function signIn(userId: string): Promise<DevSession> {
  const response = await fetch('/api/plt/v1/dev/sign-in', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ userId }),
  });
  if (!response.ok) throw new Error(`HTTP ${String(response.status)}`);
  const session = (await response.json()) as DevSession;
  sessionStorage.setItem(storageKey, JSON.stringify(session));
  notifySessionChange();
  return session;
}

/** fetch() for the API with the dev bearer token when one is held (module screens use it until MSAL lands). */
export function apiFetch(input: string, init: RequestInit = {}): Promise<Response> {
  const session = readSession();
  const headers = new Headers(init.headers);
  if (session) headers.set('Authorization', `Bearer ${session.accessToken}`);
  return fetch(input, { ...init, headers });
}
