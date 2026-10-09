import {
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
  type InfiniteData,
} from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type {
  PackActivationDecideRequest,
  PackActivationDecideResponse,
  PackActivationView,
  PackGetResponse,
  PackListPage,
  PackRollbackRequest,
  PackRollbackResponse,
  PackScheduleActivationRequest,
  PackScheduleActivationResponse,
} from '../../../api/types';

const mkt = '/api/mkt/v1';
const packPages = (data: InfiniteData<PackListPage, string | null>): PackListPage => ({
  ...(data.pages.at(-1) ?? { items: [], nextCursor: null }),
  items: data.pages.flatMap((page) => page.items),
});

/** mkt.Pack.list: every pack with its versions and the active version per legal entity. */
export function usePacks() {
  return useInfiniteQuery({
    queryKey: ['mkt', 'packs'],
    initialPageParam: null as string | null,
    queryFn: ({ signal, pageParam }) =>
      apiRequest<PackListPage>(`${mkt}/packs`, {
        signal,
        ...(pageParam ? { query: { cursor: pageParam } } : {}),
      }),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    select: packPages,
  });
}

/** mkt.Pack.get: one pack with its activation history (the pack id is a uuid; no personal data in the URL). */
export function usePack(packId: string) {
  return useQuery({
    queryKey: ['mkt', 'pack', packId],
    queryFn: ({ signal }) =>
      apiRequest<PackGetResponse>(`${mkt}/packs/${encodeURIComponent(packId)}`, { signal }),
  });
}

/** mkt.PackActivation.get: one activation request with its approval and resulting hash. */
export function usePackActivation(activationId: string) {
  return useQuery({
    queryKey: ['mkt', 'activation', activationId],
    queryFn: ({ signal }) =>
      apiRequest<PackActivationView>(
        `${mkt}/pack-activations/${encodeURIComponent(activationId)}`,
        { signal },
      ),
  });
}

/** Marks every pack read stale after a request or a decision. */
export function useRefreshPacks() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['mkt'] });
}

export type ActivationKind = 'ACTIVATE' | 'ROLLBACK';

export interface ActivationInput {
  kind: ActivationKind;
  pack: string;
  legalEntity: string;
  version: string;
  reason: string;
}

/** The two request shapes share one result: preview (dry run) or the created PENDING_APPROVAL activation. */
export type ActivationResponse = PackRollbackResponse | PackScheduleActivationResponse;

/**
 * mkt.Pack.rollback / mkt.Pack.scheduleActivation. With `dryRun` nothing is stored and the preview comes back;
 * without it a PENDING_APPROVAL activation is created for a checker (D-SL5-08).
 */
export function requestActivation(
  input: ActivationInput,
  dryRun: boolean,
  idempotencyKey: string,
): Promise<ActivationResponse> {
  const query = dryRun ? { dryRun: true } : undefined;
  if (input.kind === 'ROLLBACK') {
    const body: PackRollbackRequest = {
      pack: input.pack,
      legalEntity: input.legalEntity,
      toVersion: input.version,
      reason: input.reason,
    };
    return apiRequest<PackRollbackResponse>(`${mkt}/packs/rollback`, {
      method: 'POST',
      body,
      idempotencyKey,
      ...(query ? { query } : {}),
    });
  }
  const body: PackScheduleActivationRequest = {
    pack: input.pack,
    legalEntity: input.legalEntity,
    version: input.version,
    reason: input.reason,
  };
  return apiRequest<PackScheduleActivationResponse>(`${mkt}/packs/schedule-activation`, {
    method: 'POST',
    body,
    idempotencyKey,
    ...(query ? { query } : {}),
  });
}

/** mkt.PackActivation.decide (Platform.DesignAuthority, never the maker; the server enforces both). */
export function useDecideActivation() {
  return useMutation({
    mutationFn: ({ body, key }: { body: PackActivationDecideRequest; key: string }) =>
      apiRequest<PackActivationDecideResponse>(`${mkt}/pack-activations/decide`, {
        method: 'POST',
        body,
        idempotencyKey: key,
      }),
  });
}
