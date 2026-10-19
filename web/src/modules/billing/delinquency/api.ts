import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';

const base = '/api/bil/v1/delinquencies';

/**
 * TODO(hardening): the bil.yaml Delinquency schemas are still untyped (`process`/`data` Unspecified) while
 * SL6-BIL-DELINQ is in flight. These thin local types follow the SL6 plan defaults (D-SL6-03/04); replace them
 * with `components['schemas']['Delinquency*']` from api/generated/bil once the contract is typed.
 */
export type DelinquencyStatus =
  | 'OPEN'
  | 'NOTICE_SENT'
  | 'NOTICE_RETURNED'
  | 'ARRANGED'
  | 'ON_HOLD'
  | 'CANCELLATION_PENDING'
  | 'RESOLVED';

export interface Money {
  amount: string;
  currency: string;
}

export interface TimelineEntry {
  at: string;
  kind: string;
  actor?: string | null;
  note?: string | null;
}

export interface DelinquencyCase {
  delinquencyId: string;
  billingAccountId: string;
  policyTermId?: string | null;
  status: DelinquencyStatus;
  openAmount: Money;
  startedAt: string;
  /** Notification date from registered-post proof; absent means no clock (D-SL6-03). */
  notificationDate?: string | null;
  /** End of the one-month waiting period, Europe/Athens (D-SL6-04). */
  clockDueAt?: string | null;
  onHold?: boolean;
  timeline?: TimelineEntry[];
}

export interface DelinquencyListPage {
  items: DelinquencyCase[];
}

export interface RecordContactRequest {
  delinquencyId: string;
  channel: 'PHONE' | 'EMAIL' | 'IN_PERSON';
  outcome: string;
  note?: string;
}
export interface ArrangeRequest {
  delinquencyId: string;
  promisedDate: string;
  note?: string;
}
export interface ReleaseHoldRequest {
  delinquencyId: string;
  reason: string;
}

export function useDelinquencies(status?: DelinquencyStatus) {
  return useQuery({
    queryKey: ['bil', 'delinquencies', status ?? 'ALL'],
    queryFn: ({ signal }) =>
      apiRequest<DelinquencyListPage>(base, {
        query: { ...(status ? { status } : {}), limit: 100 },
        signal,
      }),
  });
}

export function useDelinquency(id: string) {
  return useQuery({
    queryKey: ['bil', 'delinquency', id],
    queryFn: async ({ signal }) => {
      const r = await apiRequest<{ delinquency?: DelinquencyCase } & Partial<DelinquencyCase>>(
        `${base}/${encodeURIComponent(id)}`,
        { signal },
      );
      return (r.delinquency ?? r) as DelinquencyCase;
    },
  });
}

type Action = 'record-contact' | 'arrange' | 'release-hold';

export function useDelinquencyAction<T extends { delinquencyId: string }>(
  action: Action,
  onDone: () => void,
) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ request, key }: { request: T; key: string }) =>
      apiRequest<unknown>(`${base}/${action}`, {
        method: 'POST',
        body: request,
        idempotencyKey: key,
      }),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['bil'] });
      onDone();
    },
  });
}
