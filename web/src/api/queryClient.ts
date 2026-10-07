import { QueryClient } from '@tanstack/react-query';

import { isApiError } from './client';

/** Retry only what can succeed later: never a 4xx answer (403 no permission, 404, 422), at most twice otherwise. */
export function shouldRetry(failureCount: number, error: unknown): boolean {
  if (isApiError(error) && error.status >= 400 && error.status < 500) return false;
  return failureCount < 2;
}

export function createQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: shouldRetry } } });
}
