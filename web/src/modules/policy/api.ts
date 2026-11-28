import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type {
  PartyGetResponse,
  PolicyGetResponse,
  PolicySearchCriteria,
  PolicySearchPage,
} from '../../api/types';

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

const searchPageSize = 25;

/**
 * pol.Policy.searchByCriteria (D-SLC-05: criteria in the body). Keyset paging: each "load more" sends the previous
 * page's `nextCursor`. Disabled until `criteria` is set.
 */
export function usePolicySearch(criteria: PolicySearchCriteria | null) {
  return useInfiniteQuery({
    queryKey: ['pol', 'search', criteria],
    enabled: criteria !== null,
    initialPageParam: null as string | null,
    getNextPageParam: (last: PolicySearchPage) => last.nextCursor,
    queryFn: ({ pageParam, signal }) =>
      apiRequest<PolicySearchPage>('/api/pol/v1/policies/search', {
        method: 'POST',
        body: criteria ?? {},
        query: { limit: searchPageSize, ...(pageParam ? { cursor: pageParam } : {}) },
        signal,
      }),
  });
}

/** A display name from the name forms of a party (organisation name, else «given family»). */
export function partyDisplayName(party: PartyGetResponse['party']): string {
  const n = party.names[0];
  if (!n) return party.partyNumber;
  const person = [n.givenNames, n.familyName].filter(Boolean).join(' ');
  return n.organisationName ?? (person !== '' ? person : (n.tradeName ?? party.partyNumber));
}
