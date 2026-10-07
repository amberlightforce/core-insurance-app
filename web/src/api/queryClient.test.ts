import { describe, expect, it } from 'vitest';

import { ApiError } from './client';
import { shouldRetry } from './queryClient';

const failure = (status: number) =>
  new ApiError({ status, code: 'X', title: 'x', retryable: false });

describe('shouldRetry', () => {
  it('never retries a 4xx answer (403 shows «no permission» at once, never an endless loading)', () => {
    for (const status of [400, 403, 404, 422]) {
      expect(shouldRetry(0, failure(status))).toBe(false);
    }
  });

  it('retries network and server failures at most twice', () => {
    expect(shouldRetry(0, failure(0))).toBe(true);
    expect(shouldRetry(1, failure(503))).toBe(true);
    expect(shouldRetry(2, failure(503))).toBe(false);
    expect(shouldRetry(0, new Error('boom'))).toBe(true);
  });
});
