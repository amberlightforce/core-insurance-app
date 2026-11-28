import { useQuery } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { ComplaintItem } from '../complaints/api';

/**
 * Compliance landing (NX-UI-09): complaints (cmp.Complaint.query) and statutory clocks (cmp.Clock.query).
 * TODO(hardening): the complaint query item is Unspecified in the contract, so its fields are read defensively;
 * the complaint and clock commands (classify, acknowledge, reply, escalate, close, pause, extend…) follow once
 * the module serves them.
 */
const root = '/api/cmp/v1';

export interface ClockItem {
  instanceId: string;
  clockCode: string;
  subjectRef?: { type?: string; id?: string } | string | null;
  status: string;
  kind?: string;
  deadline?: string | null;
  startedAt?: string | null;
}

export type { ComplaintItem };

export interface Page<T> {
  items: T[];
  nextCursor?: string | null;
}

export function useClocks() {
  return useQuery({
    queryKey: ['cmp', 'clock', 'query'],
    queryFn: ({ signal }) =>
      apiRequest<Page<ClockItem>>(`${root}/clocks/query`, { query: { limit: 200 }, signal }),
  });
}

export function useComplaints() {
  return useQuery({
    queryKey: ['cmp', 'complaint', 'query'],
    queryFn: ({ signal }) =>
      apiRequest<Page<ComplaintItem>>(`${root}/complaints/query`, {
        query: { limit: 200 },
        signal,
      }),
  });
}

/** Clocks still ticking: the ones the officer has to watch. */
export const isOpenClock = (c: ClockItem) =>
  c.status === 'RUNNING' || c.status === 'WARNED' || c.status === 'PAUSED';

/** A scalar from an opaque contract value: a string/number, or the first scalar among likely keys. */
export function scalar(value: unknown, keys: readonly string[] = []): string {
  if (typeof value === 'string' || typeof value === 'number') return String(value);
  if (value && typeof value === 'object') {
    for (const key of keys) {
      const v = (value as Record<string, unknown>)[key];
      if (typeof v === 'string' || typeof v === 'number') return String(v);
    }
  }
  return '';
}
