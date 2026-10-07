import { useCallback, useRef, useState } from 'react';

export interface SubmitGuard {
  isSubmitting: boolean;
  /** Runs `action` once; repeat calls while it runs are ignored. The same idempotency key is reused on retry. */
  run: (action: (idempotencyKey: string) => Promise<unknown>) => Promise<void>;
  /** Forget the key after a successful submit so the next submit is a new operation. */
  reset: () => void;
}

function newKey(): string {
  return globalThis.crypto.randomUUID();
}

/**
 * Double-submit guard (Part 2 §4.1): loading within one frame, repeat clicks ignored, and an idempotency
 * key generated on the first click and reused on retry (contract §3.5.3). Pair with `<Button isLoading>`.
 */
export function useSubmitGuard(): SubmitGuard {
  const [isSubmitting, setSubmitting] = useState(false);
  const running = useRef(false);
  const key = useRef<string | null>(null);

  const run = useCallback(async (action: (idempotencyKey: string) => Promise<unknown>) => {
    if (running.current) return;
    running.current = true;
    key.current ??= newKey();
    setSubmitting(true);
    try {
      await action(key.current);
      key.current = null;
    } finally {
      running.current = false;
      setSubmitting(false);
    }
  }, []);

  const reset = useCallback(() => {
    key.current = null;
  }, []);

  return { isSubmitting, run, reset };
}
