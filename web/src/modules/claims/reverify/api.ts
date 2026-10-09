import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest, isApiError } from '../../../api/client';
import type { components as Clm } from '../../../api/generated/clm';
import type { components as Pol } from '../../../api/generated/pol';

export type ReverifyRequest = Clm['schemas']['CoverageReverifyRequest'];
export type ReverifyResponse = Clm['schemas']['CoverageReverifyResponse'];
export type PendingReverification = NonNullable<
  Clm['schemas']['ClaimView']['pendingReverification']
>;
export type SnapshotResponse = Pol['schemas']['SnapshotGetResponse'];

/**
 * pol.Snapshot.get by snapshotRef: re-reads the same immutable view byte for byte (POL P5). The ref is opaque (policy,
 * segment, validAt, knownAt) and carries no personal data, so it may travel in the query string.
 */
export function useSnapshot(snapshotRef: string, enabled: boolean) {
  return useQuery({
    queryKey: ['pol', 'snapshot', snapshotRef],
    enabled,
    staleTime: Infinity, // an immutable view never goes stale
    queryFn: ({ signal }) =>
      apiRequest<SnapshotResponse>('/api/pol/v1/snapshots/get', {
        query: { snapshotRef },
        signal,
      }),
  });
}

export function reverify(request: ReverifyRequest, idempotencyKey: string) {
  return apiRequest<ReverifyResponse>('/api/clm/v1/coverage/reverify', {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

/** The decision changes the claim (snapshot status, coverage flags): the claim file is reloaded afterwards. */
export function useReverify(claimId: string) {
  const client = useQueryClient();
  const refresh = () => client.invalidateQueries({ queryKey: ['clm', 'claim', claimId] });
  return useMutation({
    mutationFn: ({ request, key }: { request: ReverifyRequest; key: string }) =>
      reverify(request, key),
    onSuccess: () => refresh(),
    onError: (error) => {
      // POL superseded again, or the claim changed meanwhile: show the current state before the next attempt.
      if (
        isApiError(error) &&
        (error.code === 'CLM-ERR-SNAPSHOT-MISMATCH' || error.code === 'CLM-ERR-STALE')
      ) {
        void refresh();
      }
    },
  });
}
