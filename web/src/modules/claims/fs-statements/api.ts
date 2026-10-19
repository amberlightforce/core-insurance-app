import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest, isApiError } from '../../../api/client';
import type { components as Clm } from '../../../api/generated/clm';

/**
 * Friendly Settlement statements (SL4-UI-CLM-FS, REQ-CLM-160, -264). The browser sends the period, ids, the record
 * version and the net the approver saw; the lines, nets and matching always come from the server.
 */
type S = Clm['schemas'];
export type FsStatementView = S['FsStatementView'];
export type FsStatementLine = S['FsStatementLine'];
export type FsNetView = S['FsNetView'];
export type FsApproveNetResponse = S['FsApproveNetResponse'];

const base = '/api/clm/v1/friendly-settlements';

export function isAllocationError(error: unknown): boolean {
  return isApiError(error) && (error.code ?? '').includes('ALLOCATION');
}

export function isStaleError(error: unknown): boolean {
  return isApiError(error) && (error.code ?? '').includes('STALE');
}

/** Calendar month «2026-10» → half-open period [2026-10-01, 2026-11-01). */
export function monthPeriod(month: string): { from: string; to: string } | null {
  const m = /^(\d{4})-(0[1-9]|1[0-2])$/.exec(month.trim());
  if (!m) return null;
  const year = Number(m[1]);
  const mon = Number(m[2]);
  const nextYear = mon === 12 ? year + 1 : year;
  const nextMon = mon === 12 ? 1 : mon + 1;
  const pad = (n: number) => String(n).padStart(2, '0');
  return { from: `${String(year)}-${pad(mon)}-01`, to: `${String(nextYear)}-${pad(nextMon)}-01` };
}

const key = (id: string | null) => ['clm', 'fs-statement', id];

export function useFsStatement(id: string | null) {
  return useQuery({
    queryKey: key(id),
    enabled: id !== null,
    retry: false,
    queryFn: async ({ signal }) => {
      const r = await apiRequest<{ statement: FsStatementView }>(`${base}/statements/${id ?? ''}`, {
        signal,
      });
      return r.statement;
    },
  });
}

export function useImportStatement() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({
      period,
      key: idempotencyKey,
    }: {
      period: { from: string; to: string };
      key: string;
    }) => {
      const r = await apiRequest<{ statement: FsStatementView }>(`${base}/import-statement`, {
        method: 'POST',
        body: { period },
        idempotencyKey,
      });
      return r.statement;
    },
    onSuccess: (statement) => {
      client.setQueryData(key(statement.fsStatementId), statement);
    },
  });
}

export function useApproveNet(statement: FsStatementView) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ net, key: idempotencyKey }: { net: FsNetView; key: string }) =>
      apiRequest<FsApproveNetResponse>(`${base}/approve-net`, {
        method: 'POST',
        body: {
          fsStatementId: statement.fsStatementId,
          counterpartyCode: net.counterpartyCode,
          expectedRecordVersion: statement.recordVersion,
          expectedNet: net.amount,
        },
        idempotencyKey,
      }),
    onSuccess: (r) => {
      client.setQueryData(key(r.statement.fsStatementId), r.statement);
    },
    onError: () => client.invalidateQueries({ queryKey: key(statement.fsStatementId) }),
  });
}
