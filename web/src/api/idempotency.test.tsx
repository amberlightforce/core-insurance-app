import { renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { newIdempotencyKey, useIdempotencyKey } from './idempotency';

describe('Idempotency-Key', () => {
  it('creates RFC 4122 UUIDs', () => {
    expect(newIdempotencyKey()).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
    );
  });

  it('reuses the key for the same payload (a retry) and issues a new one for a changed payload or a finished action', () => {
    const { result } = renderHook(() => useIdempotencyKey());
    const first = result.current.keyFor({ amount: '10.00' });
    expect(result.current.keyFor({ amount: '10.00' })).toBe(first);
    const changed = result.current.keyFor({ amount: '11.00' });
    expect(changed).not.toBe(first);
    result.current.release();
    expect(result.current.keyFor({ amount: '11.00' })).not.toBe(changed);
  });
});
