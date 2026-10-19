import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';

const base = '/api/bil/v1/delinquencies';

/**
 * Shapes of the SL6-BIL-DELINQ API. The bil.yaml schemas wrap everything in an untyped `process` / `data`
 * (PRD wording), so these are hand-written from DelinquencyReader.ViewAsync and the command handlers.
 * TODO(hardening): replace with generated types once bil.yaml types the delinquency view.
 */
export type DelinquencyState =
  | 'NOTICE_REQUESTED'
  | 'CLOCK_RUNNING'
  | 'NOTICE_RETURNED'
  | 'ON_HOLD'
  | 'ARRANGED'
  | 'CANCELLATION_REQUESTED'
  | 'RESOLVED';

export interface Money {
  amount: string;
  currency: string;
}

export interface DelinquencyContact {
  contactId: string;
  channel: string;
  outcome: string;
  note?: string | null;
  recordedBy?: string | null;
  recordedAt: string;
}

export interface DelinquencyCase {
  delinquencyId: string;
  billingAccountId: string;
  policyId?: string | null;
  termId?: string | null;
  state: DelinquencyState;
  trigger?: string | null;
  overdueAmount: Money;
  noticeDocumentId?: string | null;
  /** yyyy-MM-dd from registered-post proof; absent means no clock (D-SL6-03). */
  notificationDate?: string | null;
  /** End of the one-month waiting period (D-SL6-04); absent when no clock runs. */
  deadline?: string | null;
  promiseDate?: string | null;
  holdReason?: string | null;
  outcome?: string | null;
  openedAt: string;
  resolvedAt?: string | null;
  /** Only on the detail read. */
  contacts?: DelinquencyContact[] | null;
}

export interface DelinquencyListPage {
  items: { process: DelinquencyCase }[];
}

export interface RecordContactRequest {
  data: { delinquencyId: string; channel: string; outcome: string; note?: string };
}
export interface ArrangeRequest {
  data: { delinquencyId: string; promiseDate: string; note?: string };
}
/** `release: true` lifts the hold; otherwise `reason` places one. */
export interface HoldRequest {
  data: { delinquencyId: string; release?: boolean; reason?: string };
}

export function useDelinquencies(state?: DelinquencyState) {
  return useQuery({
    queryKey: ['bil', 'delinquencies', state ?? 'ALL'],
    queryFn: async ({ signal }) => {
      const page = await apiRequest<DelinquencyListPage>(base, {
        query: { ...(state ? { state } : {}), limit: 100 },
        signal,
      });
      return page.items.map((i) => i.process);
    },
  });
}

export function useDelinquency(id: string) {
  return useQuery({
    queryKey: ['bil', 'delinquency', id],
    queryFn: async ({ signal }) =>
      (
        await apiRequest<{ process: DelinquencyCase }>(`${base}/${encodeURIComponent(id)}`, {
          signal,
        })
      ).process,
  });
}

type Action = 'record-contact' | 'arrange' | 'release-hold';

export function useDelinquencyAction(action: Action, onDone: () => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({
      request,
      key,
    }: {
      request: RecordContactRequest | ArrangeRequest | HoldRequest;
      key: string;
    }) =>
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

export const delinquencyStates: DelinquencyState[] = [
  'NOTICE_REQUESTED',
  'CLOCK_RUNNING',
  'NOTICE_RETURNED',
  'ARRANGED',
  'ON_HOLD',
  'CANCELLATION_REQUESTED',
  'RESOLVED',
];
