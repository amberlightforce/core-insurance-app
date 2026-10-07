import { useCallback, useRef } from 'react';

/** A new RFC 4122 UUID for an Idempotency-Key. */
export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

/**
 * One Idempotency-Key per user action, reused on retry (D-API idempotency): while the request payload is
 * unchanged the same key is returned, so a retry after a timeout replays the original outcome. A changed payload
 * is a new action and gets a new key; a finished action calls `release()` so the next one starts fresh.
 */
export function useIdempotencyKey(): {
  keyFor: (payload: unknown) => string;
  release: () => void;
} {
  const current = useRef<{ fingerprint: string; key: string } | null>(null);
  const keyFor = useCallback((payload: unknown) => {
    const fingerprint = JSON.stringify(payload);
    if (current.current?.fingerprint !== fingerprint) {
      current.current = { fingerprint, key: newIdempotencyKey() };
    }
    return current.current.key;
  }, []);
  const release = useCallback(() => {
    current.current = null;
  }, []);
  return { keyFor, release };
}
