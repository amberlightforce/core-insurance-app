import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type {
  ClosePeriodView,
  CloseTaskCode,
  EarningStatus,
  FinancePeriod,
  IntakeException,
  ManualJournalDecideRequest,
  ManualJournalEnvelope,
  ManualJournalSubmitRequest,
  Page,
  TrialBalance,
} from './types';

const base = '/api/fin/v1';

/** fin.Balance.trialBalance for one calendar month (`yyyy-MM`) and optionally one book. */
export function useTrialBalance(period: string, book: string) {
  return useQuery({
    queryKey: ['fin', 'trial-balance', period, book],
    queryFn: ({ signal }) =>
      apiRequest<TrialBalance>(`${base}/balances/trial-balance`, {
        query: { period, book: book || undefined },
        signal,
      }),
  });
}

/** fin.Period.list: accounting periods and their state, newest first. */
export function usePeriods() {
  return useQuery({
    queryKey: ['fin', 'periods'],
    queryFn: ({ signal }) =>
      apiRequest<Page<FinancePeriod>>(`${base}/periods`, { query: { limit: 36 }, signal }),
  });
}

/** fin.IntakeException.list: business events suspended with a reason. */
export function useIntakeExceptions() {
  return useQuery({
    queryKey: ['fin', 'intake-exceptions'],
    queryFn: ({ signal }) =>
      apiRequest<Page<IntakeException>>(`${base}/intake-exceptions`, {
        query: { limit: 100 },
        signal,
      }),
  });
}

/** fin.ManualJournal.list. */
export function useManualJournals() {
  return useQuery({
    queryKey: ['fin', 'manual-journals'],
    queryFn: ({ signal }) =>
      apiRequest<Page<ManualJournalEnvelope>>(`${base}/manual-journals`, {
        query: { limit: 100 },
        signal,
      }),
  });
}

/** fin.ManualJournal.get. */
export function useManualJournal(id: string) {
  return useQuery({
    queryKey: ['fin', 'manual-journal', id],
    queryFn: ({ signal }) =>
      apiRequest<ManualJournalEnvelope>(`${base}/manual-journals/${encodeURIComponent(id)}`, {
        signal,
      }),
  });
}

/** fin.ManualJournal.create: stores a balanced, evidenced DRAFT; nothing is posted (submit and decide are BO-FIN-01b). */
export function useCreateManualJournal(onDone: (r: ManualJournalEnvelope) => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ request, key }: { request: ManualJournalSubmitRequest; key: string }) =>
      apiRequest<ManualJournalEnvelope>(`${base}/manual-journals`, {
        method: 'POST',
        body: request,
        idempotencyKey: key,
      }),
    onSuccess: (response) => {
      void client.invalidateQueries({ queryKey: ['fin', 'manual-journals'] });
      onDone(response);
    },
  });
}

/** fin.ManualJournal.submit: the maker proposes the journal; it posts only after another user approves it. */
export function useSubmitManualJournal(onDone: (r: ManualJournalEnvelope) => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ request, key }: { request: ManualJournalSubmitRequest; key: string }) =>
      apiRequest<ManualJournalEnvelope>(`${base}/manual-journals/submit`, {
        method: 'POST',
        body: request,
        idempotencyKey: key,
      }),
    onSuccess: (response) => {
      void client.invalidateQueries({ queryKey: ['fin', 'manual-journals'] });
      onDone(response);
    },
  });
}

/** fin.ManualJournal.decide: the checker approves (posting the journal) or rejects. The server enforces four-eyes. */
export function useDecideManualJournal(onDone: (r: ManualJournalEnvelope) => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ request, key }: { request: ManualJournalDecideRequest; key: string }) =>
      apiRequest<ManualJournalEnvelope>(`${base}/manual-journals/decide`, {
        method: 'POST',
        body: request,
        idempotencyKey: key,
      }),
    onSuccess: (response) => {
      void client.invalidateQueries({ queryKey: ['fin', 'manual-journals'] });
      void client.invalidateQueries({ queryKey: ['fin', 'manual-journal'] });
      onDone(response);
    },
  });
}

/** fin.ManualJournal.withdraw: the maker takes a submitted journal back. */
export function useWithdrawManualJournal(onDone: (r: ManualJournalEnvelope) => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, key }: { id: string; key: string }) =>
      apiRequest<ManualJournalEnvelope>(`${base}/manual-journals/withdraw`, {
        method: 'POST',
        body: { id },
        idempotencyKey: key,
      }),
    onSuccess: (response) => {
      void client.invalidateQueries({ queryKey: ['fin', 'manual-journals'] });
      void client.invalidateQueries({ queryKey: ['fin', 'manual-journal'] });
      onDone(response);
    },
  });
}

export type IntakeAction = 'rerun' | 'mark-duplicate' | 'reject';

export type IntakeOutcomeCode =
  | 'POSTED'
  | 'WAITING'
  | 'STILL_SUSPENDED'
  | 'NO_OP'
  | 'NOT_FOUND'
  | 'MARKED_DUPLICATE'
  | 'REJECTED';

/** One outcome per requested event (fin.IntakeException.* response). */
export interface IntakeActionResponse {
  outcomes: { eventId: string; outcome: IntakeOutcomeCode; detail?: string }[];
}

/** fin.IntakeException.rerun / markDuplicate / reject for the selected events. */
export function useIntakeAction(onDone: (response: IntakeActionResponse) => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({
      action,
      itemIds,
      reason,
      key,
    }: {
      action: IntakeAction;
      itemIds: string[];
      reason?: string;
      key: string;
    }) =>
      apiRequest<IntakeActionResponse>(`${base}/intake-exceptions/${action}`, {
        method: 'POST',
        body: reason ? { itemIds, reason } : { itemIds },
        idempotencyKey: key,
      }),
    onSuccess: (response) => {
      void client.invalidateQueries({ queryKey: ['fin', 'intake-exceptions'] });
      onDone(response);
    },
  });
}

export type CloseAction = 'soft-close' | 'lock' | 'reopen';

/** fin.Close.tasks: the checklist, state and pending reopen request of one period. */
export function useCloseTasks(period: string) {
  return useQuery({
    queryKey: ['fin', 'close-tasks', period],
    queryFn: ({ signal }) =>
      apiRequest<ClosePeriodView>(`${base}/close/tasks`, { query: { period }, signal }),
  });
}

function useInvalidateClose() {
  const client = useQueryClient();
  return () => {
    void client.invalidateQueries({ queryKey: ['fin', 'periods'] });
    void client.invalidateQueries({ queryKey: ['fin', 'close-tasks'] });
  };
}

/** fin.Close.softClose / lock / reopen for one period; a reason is always sent. */
export function useCloseAction(onDone: () => void) {
  const invalidate = useInvalidateClose();
  return useMutation({
    mutationFn: ({
      action,
      period,
      reason,
      key,
    }: {
      action: CloseAction;
      period: string;
      reason: string;
      key: string;
    }) =>
      apiRequest<ClosePeriodView>(`${base}/close/${action}`, {
        method: 'POST',
        body: { period, reason },
        idempotencyKey: key,
      }),
    onSuccess: () => {
      invalidate();
      onDone();
    },
  });
}

/** fin.Close.signOff: confirms one checklist task of a period. */
export function useSignOffTask() {
  const invalidate = useInvalidateClose();
  return useMutation({
    mutationFn: ({ period, task, key }: { period: string; task: CloseTaskCode; key: string }) =>
      apiRequest<ClosePeriodView>(`${base}/close/sign-off`, {
        method: 'POST',
        body: { period, task },
        idempotencyKey: key,
      }),
    onSuccess: invalidate,
  });
}

/** fin.Earning.status: the latest earning run. */
export function useEarningStatus() {
  return useQuery({
    queryKey: ['fin', 'earning-status'],
    queryFn: ({ signal }) => apiRequest<EarningStatus>(`${base}/earning/status`, { signal }),
  });
}

/** fin.Earning.run as of a date: posts earned premium to the books. */
export function useRunEarning(onDone: () => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ asOf, key }: { asOf: string; key: string }) =>
      apiRequest<unknown>(`${base}/earning/run`, {
        method: 'POST',
        body: { as: asOf },
        idempotencyKey: key,
      }),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['fin', 'earning-status'] });
      onDone();
    },
  });
}
