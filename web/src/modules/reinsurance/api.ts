import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo } from 'react';

import { apiRequest } from '../../api/client';
import type { components as Ri } from '../../api/generated/ri';

/**
 * Reinsurance treaty registry and recoverables (SL4-UI-RI). The server decides everything that matters: maker ≠
 * checker (RI-ERR-SOD), authority and state. The browser sends contract ids, the optimistic-lock version and a
 * reason only; it never sends a role, an approver or an amount (PITFALLS 1-7).
 */
type Schemas = Ri['schemas'];

export type ContractStatus = Schemas['RiContractStatus'];
export type ContractType = Schemas['RiContractType'];
export type ContractListItem = Schemas['ContractListItem'];
export type ContractView = Schemas['RiContractView'];
export type ContractLayer = Schemas['RiLayer'];
export type ContractParticipation = Schemas['RiParticipationView'];
export type ContractApproveRequest = Schemas['ContractApproveRequest'];
export type ContractApproveResponse = Schemas['ContractApproveResponse'];
export type ContractSubmitRequest = Schemas['ContractSubmitRequest'];
export type ContractSubmitResponse = Schemas['ContractSubmitResponse'];
export type RecoveryRow = Schemas['RecoveryListByContractItem'];
export type ClaimRecoveryRow = Schemas['RecoveryListByClaimItem'];

/** The statuses in lifecycle order (the filter and the stage strip use it). */
export const contractStatuses = [
  'DRAFT',
  'PENDING_APPROVAL',
  'APPROVED',
  'ACTIVE',
  'EXPIRED',
  'CLOSED',
] as const satisfies readonly ContractStatus[];

export function isContractStatus(value: string | null): value is ContractStatus {
  return contractStatuses.some((status) => status === value);
}

/** A generated `allOf` page types its items as unknown[]; these are the real shapes. */
export interface Page<T> {
  items: T[];
  nextCursor: string | null;
  limit?: number;
}

const root = '/api/ri/v1';
const listPageSize = 100;
const recoveryPageSize = 200;
/** A hard stop for the auto-paged recoverables (200 x 25 = 5 000 rows); the view then says it is partial. */
export const maxRecoveryPages = 25;

/** ri.Contract.list, cursor-paged, optionally filtered by status. */
export function useContractList(status: ContractStatus | null) {
  const query = useInfiniteQuery({
    queryKey: ['ri', 'contracts', 'list', status],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) =>
      apiRequest<Page<ContractListItem>>(`${root}/contracts`, {
        query: { limit: listPageSize, cursor: pageParam, status },
        signal,
      }),
    getNextPageParam: (last) => last.nextCursor,
  });
  const pages = query.data?.pages;
  // One array per fetched page set: the table never receives a fresh array while nothing changed (PITFALLS 26).
  const items = useMemo<ContractListItem[]>(
    () => pages?.flatMap((page) => page.items) ?? [],
    [pages],
  );
  return { query, items };
}

/** ri.Contract.get. */
export function useContract(contractId: string) {
  return useQuery({
    queryKey: ['ri', 'contracts', 'get', contractId],
    queryFn: ({ signal }) =>
      apiRequest<{ contract: ContractView }>(
        `${root}/contracts/${encodeURIComponent(contractId)}`,
        { signal },
      ),
    select: (data) => data.contract,
  });
}

/** ri.Contract.submit (Draft → Pending approval). One Idempotency-Key per user action. */
export function submitContract(request: ContractSubmitRequest, idempotencyKey: string) {
  return apiRequest<ContractSubmitResponse>(`${root}/contracts/submit`, {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

/** ri.Contract.approve: APPROVE, or RETURN with a reason. The server refuses the enterer (RI-ERR-SOD). */
export function approveContract(request: ContractApproveRequest, idempotencyKey: string) {
  return apiRequest<ContractApproveResponse>(`${root}/contracts/approve`, {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

/** After a command the registry, the contract and the recoverables are read again. */
export function useRefreshContracts() {
  const client = useQueryClient();
  return useCallback(() => client.invalidateQueries({ queryKey: ['ri'] }), [client]);
}

/** A command mutation shared by submit and decide, so both refresh the same keys. */
export function useContractCommand<Req>(run: (request: Req, key: string) => Promise<unknown>) {
  const refresh = useRefreshContracts();
  return useMutation({
    mutationFn: ({ body, key }: { body: Req; key: string }) => run(body, key),
    onSuccess: () => refresh(),
  });
}

/**
 * ri.Recovery.listByContract: every page is read (up to {@link maxRecoveryPages}) because the layer-year figures
 * are sums over all rows. `partial` is true when the cap was reached with more pages left.
 */
export function useContractRecoveries(contractId: string) {
  const query = useInfiniteQuery({
    queryKey: ['ri', 'recoveries', 'contract', contractId],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) =>
      apiRequest<Page<RecoveryRow>>(`${root}/recoveries/list-by-contract`, {
        query: { contractId, limit: recoveryPageSize, cursor: pageParam },
        signal,
      }),
    getNextPageParam: (last) => last.nextCursor,
  });
  const { hasNextPage, isFetchingNextPage, fetchNextPage, data } = query;
  const pageCount = data?.pages.length ?? 0;
  useEffect(() => {
    if (hasNextPage && !isFetchingNextPage && pageCount < maxRecoveryPages) void fetchNextPage();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage, pageCount]);
  const pages = data?.pages;
  const rows = useMemo<RecoveryRow[]>(() => pages?.flatMap((page) => page.items) ?? [], [pages]);
  return { query, rows, partial: hasNextPage && pageCount >= maxRecoveryPages };
}

/** ri.Recovery.listByClaim: per contract x layer x participant, the incurred and paid recoverable of one claim. */
export function useClaimRecoveries(claimId: string) {
  const query = useInfiniteQuery({
    queryKey: ['ri', 'recoveries', 'claim', claimId],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) =>
      apiRequest<Page<ClaimRecoveryRow>>(`${root}/recoveries/list-by-claim`, {
        query: { claimId, limit: recoveryPageSize, cursor: pageParam },
        signal,
      }),
    getNextPageParam: (last) => last.nextCursor,
  });
  const { hasNextPage, isFetchingNextPage, fetchNextPage, data } = query;
  const pageCount = data?.pages.length ?? 0;
  useEffect(() => {
    if (hasNextPage && !isFetchingNextPage && pageCount < maxRecoveryPages) void fetchNextPage();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage, pageCount]);
  const pages = data?.pages;
  const rows = useMemo<ClaimRecoveryRow[]>(
    () => pages?.flatMap((page) => page.items) ?? [],
    [pages],
  );
  return { query, rows };
}
