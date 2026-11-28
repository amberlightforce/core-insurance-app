import { useQuery } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components as Cmp } from '../../../api/generated/cmp';

/**
 * Complaints list (BO-CMP-01) on cmp.Complaint.query. The server filters by status only; class (category),
 * channel and overdue are narrowed in the browser over the page it returns.
 * TODO(hardening): server-side filters and cursor paging once the query takes them (today: first 200).
 */
type Schemas = Cmp['schemas'];

export type ComplaintItem = Schemas['ComplaintQueryItem'];
export type ComplaintView = Schemas['ComplaintView'];
export type ComplaintDeadline = Schemas['ComplaintDeadlineView'];

export const complaintStatuses = [
  'RECEIVED',
  'ACKNOWLEDGED',
  'UNDER_INVESTIGATION',
  'ANSWERED',
  'ESCALATED',
  'CLOSED',
] as const;

interface Page<T> {
  items: T[];
  nextCursor?: string | null;
}

export function useComplaintList(status: string) {
  return useQuery({
    queryKey: ['cmp', 'complaint', 'query', 'list', status],
    queryFn: ({ signal }) =>
      apiRequest<Page<ComplaintItem>>('/api/cmp/v1/complaints/query', {
        query: { limit: 200, ...(status ? { status } : {}) },
        signal,
      }),
  });
}

/** Clock statuses that are still ticking. */
const openClock = (d: ComplaintDeadline) =>
  d.status === 'RUNNING' || d.status === 'WARNED' || d.status === 'PAUSED';

/** The deadline the officer has to watch: the earliest ticking clock, or null when none. */
export function nextDeadline(item: ComplaintItem): ComplaintDeadline | null {
  const open = (item.deadlines ?? []).filter((d) => openClock(d) && d.deadline);
  open.sort((a, b) => (a.deadline ?? '').localeCompare(b.deadline ?? ''));
  return open[0] ?? null;
}

/** Whole days from now to the instant (negative once past), rounded toward the deadline. */
export function daysUntil(instant: string, now: number = Date.now()): number {
  const diff = Date.parse(instant) - now;
  return diff >= 0 ? Math.ceil(diff / 86_400_000) : -Math.ceil(-diff / 86_400_000);
}

/** A complaint is overdue when it is open and a clock is breached or its deadline has passed. */
export function isOverdue(item: ComplaintItem, now: number = Date.now()): boolean {
  if (item.complaint?.status === 'CLOSED') return false;
  if ((item.deadlines ?? []).some((d) => d.status === 'BREACHED')) return true;
  const next = nextDeadline(item);
  return next?.deadline ? Date.parse(next.deadline) < now : false;
}
