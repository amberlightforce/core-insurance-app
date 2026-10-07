import { keepPreviousData, useQuery } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type { PolicyGetResponse } from '../../api/types';

/**
 * pol.Policy.get as of a date. A date-form `validAt` means the end of that business day in Europe/Athens
 * (D-SLC-13), so "as of 01/01/2027" shows the policy as at close of business on that day. `knownAt` is left to
 * the server (now).
 */
export function usePolicy(policyId: string, validAt: string) {
  return useQuery({
    queryKey: ['pol', 'policy', policyId, validAt],
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) =>
      apiRequest<PolicyGetResponse>(`/api/pol/v1/policies/${encodeURIComponent(policyId)}`, {
        query: { validAt },
        signal,
      }),
  });
}
