import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type { components as Mig } from '../../api/generated/mig';

/**
 * Data-migration batches and routing (SL6-UI-MIG). The server decides everything that matters: the dry-run gate,
 * maker ≠ checker on the batch approval, authority, reversibility. The browser sends batch ids, items and a reason.
 */
type Schemas = Mig['schemas'];

export type BatchStatus = NonNullable<Schemas['BatchView']['status']>;
export type BatchView = Schemas['BatchView'];
export type BatchResponse = Schemas['BatchResponse'];
export type Outcome = Schemas['BatchOutcomesItem'];
export type OutcomeCode = Outcome['outcome'];
export type BatchCreateRequest = Schemas['BatchCreateRequest'];
export type BatchItemInput = Schemas['BatchItemInput'];
export type RoutingResolveResponse = Schemas['RoutingResolveResponse'];
export type RoutingOverrideRequest = Schemas['RoutingOverrideRequest'];
export type RoutingOverrideResponse = Schemas['RoutingOverrideResponse'];
export type RouteState = RoutingOverrideRequest['masterState'];

export const outcomeCodes = [
  'PENDING',
  'IMPORTED',
  'SKIPPED_EXISTING',
  'FAILED',
  'REVERSED',
  'REVERSAL_BLOCKED',
] as const satisfies readonly OutcomeCode[];

export const routeStates = [
  'NONE',
  'TRANSITIONING',
  'CORE',
] as const satisfies readonly RouteState[];

const root = '/api/mig/v1';

export function useBatch(batchId: string) {
  return useQuery({
    queryKey: ['mig', 'batch', batchId],
    queryFn: ({ signal }) =>
      apiRequest<BatchResponse>(`${root}/batches/${encodeURIComponent(batchId)}`, { signal }),
  });
}

export function createBatch(request: BatchCreateRequest, key: string) {
  return apiRequest<BatchResponse>(`${root}/batches`, {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

export type BatchAction = 'start' | 'approve' | 'pause' | 'resume' | 'reverse';

export interface BatchCommand {
  action: BatchAction;
  body: { batchId: string; dryRun?: boolean; decision?: 'APPROVE' | 'REJECT'; comment?: string };
}

/** mig.Batch.start / approve / pause / resume / reverse. One Idempotency-Key per user action. */
export function useBatchCommand(batchId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ command, key }: { command: BatchCommand; key: string }) =>
      apiRequest<BatchResponse>(`${root}/batches/${command.action}`, {
        method: 'POST',
        body: command.body,
        idempotencyKey: key,
      }),
    onSuccess: (data) => {
      client.setQueryData(['mig', 'batch', batchId], data);
    },
  });
}

/** mig.Routing.resolve: the master state of an object on a date (today when `date` is empty). */
export function resolveRoute(objectType: string, key: string, date: string) {
  return apiRequest<RoutingResolveResponse>(`${root}/routing/resolve`, {
    method: 'POST',
    body: { objectType, key, ...(date ? { date } : {}) },
  });
}

/** mig.Routing.override. */
export function overrideRoute(request: RoutingOverrideRequest, key: string) {
  return apiRequest<RoutingOverrideResponse>(`${root}/routing/override`, {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

const recentKey = 'coreins.migration.recentBatches';
export interface RecentBatch {
  batchId: string;
  name: string;
}

/** The API has no batch list: the screens remember the batches this browser created or opened. */
export function readRecent(): RecentBatch[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(recentKey) ?? '[]');
    return Array.isArray(parsed) ? (parsed as RecentBatch[]).slice(0, 20) : [];
  } catch {
    return [];
  }
}

export function remember(batch: RecentBatch) {
  try {
    const next = [batch, ...readRecent().filter((b) => b.batchId !== batch.batchId)].slice(0, 20);
    localStorage.setItem(recentKey, JSON.stringify(next));
  } catch {
    // Storage is a convenience only.
  }
}
