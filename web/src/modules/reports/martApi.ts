import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import { newIdempotencyKey } from '../../api/idempotency';
import type { components as Dat } from '../../api/generated/dat';

const base = '/api/dat/v1';

export const martCodes = ['PORTFOLIO_PREMIUM', 'CLAIMS_EXPERIENCE', 'RI_CESSION_SUMMARY'] as const;
export type MartCode = (typeof martCodes)[number];

export type MartRunRequest = Dat['schemas']['MartRunRequest'];
export type MartRunResponse = Dat['schemas']['MartRunResponse'];
export type MartAdjustRequest = Dat['schemas']['MartAdjustRequest'];
export type MartExportResponse = Dat['schemas']['MartExportResponse'];

/** The generated contract leaves the read shapes open (`Unspecified`); these mirror MartEngine's JSON. */
export interface MartAdjustment {
  adjustmentId: string;
  figureKey: string;
  amount: number | string;
  currency: string;
  reason: string;
  evidence: string;
  createdBy: string;
  createdAt: string;
}

export interface MartRun {
  runId: string;
  mart: MartCode;
  period: string;
  productCode: string;
  validAt: string;
  knownAt: string;
  status: string;
  snapshotHash: string;
  legalStatus: string;
  definitionVersion: string;
  figureCount: number;
  createdAt: string;
  createdBy: string;
  signOff: { signedBy: string; signedAt: string; comment?: string | null; valid: boolean } | null;
  adjustments: MartAdjustment[] | null;
}

export interface MartFigure {
  key: string;
  metric: string;
  productCode: string;
  month: string;
  dimension: string;
  value: number | string;
  adjustment: number | string;
  adjustedValue: number | string;
  unit: string;
  sourceCount: number;
}

export interface MartDetail {
  run: MartRun;
  figures: MartFigure[];
}

export interface CompareRow {
  key: string;
  a: number | null;
  b: number | null;
  delta: number;
  unit: string;
  status: 'ADDED' | 'REMOVED' | 'CHANGED';
}

export interface Compare {
  figures: { compared: number; changed: number };
  diff: CompareRow[];
}

export interface KpiValues {
  values: {
    GWP?: { amount: number | string; currency: string };
    CLAIMS_PAID?: { amount: number | string; currency: string };
    OPEN_RESERVE?: { amount: number | string; currency: string };
    LOSS_RATIO?: { ratio: number | string | null };
  };
  as?: { knownAt?: string };
}

/** dat.Kpi.get: all four KPI definitions for the caller's legal entity. */
export function useKpis() {
  return useQuery({
    queryKey: ['dat', 'kpis'],
    queryFn: ({ signal }) => apiRequest<KpiValues>(`${base}/kpis/get`, { signal }),
  });
}

/** dat.Mart.get (run header, figures with adjusted values, adjustments, sign-off). */
export function fetchRun(runId: string, signal?: AbortSignal) {
  return apiRequest<MartDetail>(`${base}/marts/${encodeURIComponent(runId)}`, {
    ...(signal ? { signal } : {}),
  });
}

export function useRunDetail(runId: string | null) {
  return useQuery({
    queryKey: ['dat', 'run', runId],
    enabled: runId !== null,
    queryFn: ({ signal }) => fetchRun(runId ?? '', signal),
  });
}

/** dat.Mart.compare of two stored runs. */
export function useCompare(runA: string | null, runB: string | null) {
  return useQuery({
    queryKey: ['dat', 'compare', runA, runB],
    enabled: runA !== null && runB !== null,
    queryFn: ({ signal }) =>
      apiRequest<Compare>(`${base}/marts/compare`, { query: { runA, runB }, signal }),
  });
}

const historyLimit = 20;

/** One row of dat.Mart.list (the run header without figures or adjustments). */
export type MartRunSummary = Pick<
  MartRun,
  | 'runId'
  | 'mart'
  | 'period'
  | 'productCode'
  | 'status'
  | 'snapshotHash'
  | 'legalStatus'
  | 'figureCount'
  | 'createdAt'
  | 'createdBy'
> & {
  signOff: { signedBy: string; signedAt: string; valid: boolean } | null;
};

/** The newest runs of the legal entity (dat.Mart.list), newest first. */
export function useRunHistory() {
  const query = useQuery({
    queryKey: ['dat', 'runs'],
    queryFn: ({ signal }) =>
      apiRequest<{ items: MartRunSummary[]; nextCursor: string | null }>(`${base}/marts/runs`, {
        query: { limit: historyLimit },
        signal,
      }),
  });
  return { runs: query.data?.items ?? [], isPending: query.isPending };
}

/** dat.Mart.run: computes a mart inline; each click is a new action with its own idempotency key. */
export function useRunMart(onDone: (runId: string) => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (request: MartRunRequest) =>
      apiRequest<MartRunResponse>(`${base}/marts/run`, {
        method: 'POST',
        body: request,
        idempotencyKey: newIdempotencyKey(),
      }),
    onSuccess: (response) => {
      void client.invalidateQueries({ queryKey: ['dat'] });
      if (response.runId) onDone(response.runId);
    },
  });
}

export function useAdjust(onDone: () => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ request, key }: { request: MartAdjustRequest; key: string }) =>
      apiRequest(`${base}/marts/adjust`, { method: 'POST', body: request, idempotencyKey: key }),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['dat'] });
      onDone();
    },
  });
}

export function useSignOff() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ runId, key }: { runId: string; key: string }) =>
      apiRequest(`${base}/marts/sign-off`, {
        method: 'POST',
        body: { runId },
        idempotencyKey: key,
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['dat'] }),
  });
}

export function useExportRun() {
  return useMutation({
    mutationFn: ({ runId, format, key }: { runId: string; format: 'CSV' | 'JSON'; key: string }) =>
      apiRequest<MartExportResponse>(`${base}/marts/export`, {
        method: 'POST',
        body: { runId, format },
        idempotencyKey: key,
      }),
  });
}

/** Saves an export response as a file (a Blob download; nothing is stored). */
export function saveExport(file: MartExportResponse): void {
  const blob = new Blob([file.content ?? ''], { type: file.contentType ?? 'text/plain' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = file.fileName ?? 'mart-export';
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}
