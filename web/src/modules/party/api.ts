import { useMutation, useQuery } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type {
  PartyCreateRequest,
  PartyCreateResponse,
  PartyGetResponse,
  PartySearchCriteria,
  PartySearchPage,
} from '../../api/types';

const base = '/api/pty/v1/parties';

/** pty.Party.searchByCriteria: names, identifiers and the single search box travel in the body (D-SLC-05). */
export function searchParties(criteria: PartySearchCriteria, signal?: AbortSignal) {
  return apiRequest<PartySearchPage>(`${base}/search`, {
    method: 'POST',
    body: criteria,
    query: { limit: 25 },
    ...(signal ? { signal } : {}),
  });
}

export function useSearchParties() {
  return useMutation({ mutationFn: (criteria: PartySearchCriteria) => searchParties(criteria) });
}

/** pty.Party.create (a command: Idempotency-Key). */
export function createParty(request: PartyCreateRequest, idempotencyKey: string) {
  return apiRequest<PartyCreateResponse>(base, { method: 'POST', body: request, idempotencyKey });
}

/** pty.Party.get: personal (P2) values come masked. */
export function useParty(partyId: string) {
  return useQuery({
    queryKey: ['party', partyId],
    queryFn: ({ signal }) =>
      apiRequest<PartyGetResponse>(`${base}/${encodeURIComponent(partyId)}`, { signal }),
  });
}

/** pty.Party.get with `revealPurpose`: an audited, unmasked read. A mutation, never cached or retried silently. */
export function useRevealParty(partyId: string) {
  return useMutation({
    mutationFn: (purpose: string) =>
      apiRequest<PartyGetResponse>(`${base}/${encodeURIComponent(partyId)}`, {
        query: { revealPurpose: purpose },
      }),
  });
}

/** Purposes offered for an audited reveal (codes match `^[A-Z][A-Z0-9_]{0,63}$`; labels are in the i18n files). */
export const revealPurposes = [
  'CUSTOMER_SERVICE',
  'UNDERWRITING',
  'CLAIMS_HANDLING',
  'COMPLIANCE_REVIEW',
  'DATA_SUBJECT_REQUEST',
] as const;
