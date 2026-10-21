import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type { components } from '../../api/generated/wrk';
import { workInboxRoles } from '../../auth/roles';
import { useDevSession } from '../../dev-auth/devAuth';

const wrk = '/api/wrk/v1';

type Schemas = components['schemas'];

export type TaskScope = 'mine' | 'queue' | 'team';
export type TaskState = 'OPEN' | 'COMPLETED' | 'SKIPPED' | 'CANCELLED';

export interface ObjectRef {
  module: string;
  type: string;
  id: string;
}

/**
 * One activity as WRK's staff inbox sends it (`activity` of the loosely typed list/get/command responses; the
 * generated contract types that member as «Unspecified», so the shape is declared here from ActivityView).
 */
export interface Task {
  id: string;
  number: string;
  patternCode: string;
  title: string;
  category: string;
  priority: string;
  state: TaskState;
  primaryObject: ObjectRef;
  secondaryObjects: ObjectRef[];
  assignee: { type: string; id: string };
  queueCode: string;
  queueId: string;
  targetAt?: string | null;
  escalationAt?: string | null;
  slaBreached: boolean;
  createdAt: string;
  createdBy: string;
  closedAt?: string | null;
  outcome?: string | null;
  note?: string | null;
  recordVersion: number;
  assignedToMe: boolean;
  claimableByMe: boolean;
}

type ListPage = Schemas['ActivityListPage'];
type GetPage = Schemas['ActivityGetPage'];

const taskOf = (item: { activity?: unknown }): Task => item.activity as Task;

/** True when the signed-in role set sees the task inbox at all (the API still filters to the caller's queues). */
export function useCanSeeInbox(): boolean {
  const roles = useDevSession()?.user.roles ?? [];
  return roles.some((r) => workInboxRoles.some((p) => p === r));
}

export const taskKeys = {
  all: ['wrk'] as const,
  list: (scope: TaskScope, state: TaskState, extra = '') =>
    ['wrk', 'activities', scope, state, extra] as const,
  one: (id: string) => ['wrk', 'activity', id] as const,
};

const filtersOf = (scope: TaskScope, state: TaskState, extra: string[] = []) =>
  [`scope:${scope}`, `state:${state}`, ...extra].join(',');

/** wrk.Activity.list for one inbox tab. Server order: priority, due date, age. */
export function useTasks(scope: TaskScope, state: TaskState = 'OPEN', enabled = true) {
  return useInfiniteQuery({
    queryKey: taskKeys.list(scope, state),
    enabled,
    initialPageParam: null as string | null,
    queryFn: ({ signal, pageParam }) =>
      apiRequest<ListPage>(`${wrk}/activities`, {
        query: { filters: filtersOf(scope, state), ...(pageParam ? { cursor: pageParam } : {}) },
        signal,
      }),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    select: (data) => ({
      tasks: data.pages.flatMap((page) => page.items.map(taskOf)),
    }),
  });
}

/**
 * Open tasks of a scope with the one page the badges and the home card read (at most 200 rows). When the server has
 * more, `more` is true and the count reads «200+».
 * TODO(hardening): a server-side count (wrk.Team.summary) once it serves counts.
 */
export function useTaskDigest(scope: TaskScope, enabled = true) {
  return useQuery({
    queryKey: taskKeys.list(scope, 'OPEN', 'digest'),
    enabled,
    refetchInterval: 60_000,
    queryFn: ({ signal }) =>
      apiRequest<ListPage>(`${wrk}/activities`, {
        query: { filters: filtersOf(scope, 'OPEN'), limit: 200 },
        signal,
      }),
    select: (page) => ({
      tasks: page.items.map(taskOf),
      more: Boolean(page.nextCursor),
    }),
  });
}

/** wrk.Activity.get. */
export function useTask(id: string) {
  return useQuery({
    queryKey: taskKeys.one(id),
    queryFn: ({ signal }) =>
      apiRequest<GetPage>(`${wrk}/activities/${encodeURIComponent(id)}`, { signal }),
    select: (page): Task => taskOf(page.items[0] ?? {}),
  });
}

/**
 * The PACK-ROLLBACK-REVIEW task WRK raised for a pack-rollback activation (primary object MKT/PackActivation/{id}),
 * or null when the caller sees none. The list has no object filter yet: one page of the queue is matched here.
 * TODO(hardening): filter by object on the server (wrk.Activity.list objectRef).
 */
export function usePackRollbackTask(activationId: string, enabled: boolean) {
  return useQuery({
    queryKey: ['wrk', 'packRollbackTask', activationId],
    enabled,
    retry: false,
    queryFn: ({ signal }) =>
      apiRequest<ListPage>(`${wrk}/activities`, {
        query: { filters: 'scope:team,state:ALL,queue:PACK-ROLLBACK-REVIEW', limit: 200 },
        signal,
      }),
    select: (page): Task | null =>
      page.items
        .map(taskOf)
        .find((t) => t.primaryObject.module === 'MKT' && t.primaryObject.id === activationId) ??
      null,
  });
}

interface CommandArgs {
  key: string;
}

function useTaskCommand(path: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ body, key }: { body: object } & CommandArgs) =>
      apiRequest<{ activity?: unknown; assignment?: unknown }>(`${wrk}${path}`, {
        method: 'POST',
        body,
        idempotencyKey: key,
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: taskKeys.all }),
  });
}

/** wrk.Activity.assign with target «me»: claim a queue task. */
export const useClaimTask = () => useTaskCommand('/activities/assign');

export interface CloseBody {
  id: string;
  outcome?: string;
  note?: string;
  reason?: string;
}

/** wrk.Activity.complete (outcome required). */
export const useCompleteTask = () => useTaskCommand('/activities/complete');

/** wrk.Activity.skip (a reason is required). */
export const useSkipTask = () => useTaskCommand('/activities/skip');

/** wrk.Queue.getNext: takes the most urgent open task of the caller's queues and assigns it to the caller. */
export function useGetNextTask() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ key }: CommandArgs) =>
      apiRequest<{ assignedActivity?: unknown }>(`${wrk}/queues/get-next`, {
        method: 'POST',
        body: {},
        idempotencyKey: key,
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: taskKeys.all }),
  });
}

export const nextTaskOf = (response: { assignedActivity?: unknown }): Task =>
  response.assignedActivity as Task;
