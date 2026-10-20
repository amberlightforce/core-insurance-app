import {
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
  type InfiniteData,
} from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components } from '../../../api/generated/chn';
import { withdrawalRoles } from '../../../auth/roles';
import { useDevSession } from '../../../dev-auth/devAuth';

type S = components['schemas'];
export type WithdrawalDeclareRequest = S['WithdrawalDeclareRequest'];
export type WithdrawalDeclareResponse = S['WithdrawalDeclareResponse'];
export type WithdrawalConfirmResponse = S['WithdrawalConfirmResponse'];
export type WithdrawalCloseResponse = S['WithdrawalCloseResponse'];
export type WithdrawalGetResponse = S['WithdrawalGetResponse'];
export type WithdrawalListItem = S['WithdrawalListItem'];
export type WithdrawalListPage = S['WithdrawalListPage'];
export type WithdrawalStatus = S['WithdrawalStatusCode'];

const chn = '/api/chn/v1/withdrawals';

/** Staff roles that may list and close withdrawal requests (permissions chn.Withdrawal.list / .close). */
export function useCanWorkWithdrawals(): boolean {
  const roles = useDevSession()?.user.roles ?? [];
  return roles.some((r) => withdrawalRoles.some((w) => w === r));
}

export function declareWithdrawal(body: WithdrawalDeclareRequest, idempotencyKey: string) {
  return apiRequest<WithdrawalDeclareResponse>(`${chn}/declare`, {
    method: 'POST',
    body,
    idempotencyKey,
  });
}

export function confirmWithdrawal(requestNumber: string, token: string, idempotencyKey: string) {
  return apiRequest<WithdrawalConfirmResponse>(`${chn}/confirm`, {
    method: 'POST',
    body: { requestNumber, token },
    idempotencyKey,
  });
}

export function useWithdrawal(id: string) {
  return useQuery({
    queryKey: ['chn', 'withdrawal', id],
    enabled: id !== '',
    queryFn: ({ signal }) =>
      apiRequest<WithdrawalGetResponse>(`${chn}/${encodeURIComponent(id)}`, { signal }),
  });
}

const pages = (data: InfiniteData<WithdrawalListPage, string | null>): WithdrawalListPage => ({
  ...(data.pages.at(-1) ?? { items: [], nextCursor: null }),
  items: data.pages.flatMap((page) => page.items),
});

/** chn.Withdrawal.list; `requestNumber` narrows to one request. */
export function useWithdrawals(requestNumber: string) {
  return useInfiniteQuery({
    queryKey: ['chn', 'withdrawals', requestNumber],
    initialPageParam: null as string | null,
    queryFn: ({ signal, pageParam }) =>
      apiRequest<WithdrawalListPage>(chn, {
        query: {
          limit: 25,
          ...(requestNumber ? { requestNumber } : {}),
          ...(pageParam ? { cursor: pageParam } : {}),
        },
        signal,
      }),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    select: pages,
  });
}

/** chn.Withdrawal.close: records the POL outcome (staff). */
export function useCloseWithdrawal() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({
      id,
      outcome,
      key,
    }: {
      id: string;
      outcome: 'PROCESSED' | 'REJECTED';
      key: string;
    }) =>
      apiRequest<WithdrawalCloseResponse>(`${chn}/${encodeURIComponent(id)}/close`, {
        method: 'POST',
        body: { outcome },
        idempotencyKey: key,
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['chn'] }),
  });
}
