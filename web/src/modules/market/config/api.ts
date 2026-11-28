import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import { newIdempotencyKey } from '../../../api/idempotency';
import type {
  ConfigChangeItemInput,
  ConfigChangeListPage,
  ConfigChangePreviewRun,
  ConfigChangeRebaseResponse,
  ConfigChangeRequestView,
} from '../../../api/types';

const base = '/api/mkt/v1/config-change-requests';

/** mkt.ConfigChangeRequest.list, newest first; `status` is the optional filter. */
export function useConfigChanges(status: string) {
  return useInfiniteQuery({
    queryKey: ['mkt', 'config-changes', status],
    initialPageParam: null as string | null,
    queryFn: ({ signal, pageParam }) =>
      apiRequest<ConfigChangeListPage>(base, {
        signal,
        query: { ...(status ? { status } : {}), ...(pageParam ? { cursor: pageParam } : {}) },
      }),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    select: (data) => ({
      items: data.pages.flatMap((p) => p.items),
    }),
  });
}

/** mkt.ConfigChangeRequest.get. */
export function useConfigChange(id: string) {
  return useQuery({
    queryKey: ['mkt', 'config-change', id],
    enabled: id !== '',
    queryFn: ({ signal }) =>
      apiRequest<ConfigChangeRequestView>(`${base}/${encodeURIComponent(id)}`, { signal }),
  });
}

/** mkt.ConfigChangeRequest.preview: the diff against the newest state (writes nothing). */
export function useConfigPreview(id: string, enabled: boolean, version: number) {
  return useQuery({
    queryKey: ['mkt', 'config-change-preview', id, version],
    enabled,
    queryFn: ({ signal }) =>
      apiRequest<{ previewRun: ConfigChangePreviewRun }>(`${base}/preview`, {
        method: 'POST',
        body: { requestId: id },
        idempotencyKey: newIdempotencyKey(),
        signal,
      }).then((r) => r.previewRun),
  });
}

export type ConfigAction = 'submit' | 'approve' | 'reject' | 'withdraw' | 'rebase';

export interface ConfigActionResult {
  request: ConfigChangeRequestView;
  conflicts?: ConfigChangeRebaseResponse['conflicts'];
}

/** The five decisions on one request; each needs the request id and (all but submit) a reason. */
export function useConfigAction() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({
      action,
      body,
      key,
    }: {
      action: ConfigAction;
      body: { requestId: string; reason?: string };
      key: string;
    }) =>
      apiRequest<ConfigActionResult>(`${base}/${action}`, {
        method: 'POST',
        body,
        idempotencyKey: key,
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['mkt'] }),
  });
}

export interface ConfigDraft {
  reason: string;
  items: ConfigChangeItemInput[];
}

/** mkt.ConfigChangeRequest.create (no id) or update (a DRAFT, maker only). */
export function useSaveConfigChange(id: string | undefined) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ draft, key }: { draft: ConfigDraft; key: string }) =>
      apiRequest<{ request: ConfigChangeRequestView }>(
        id ? `${base}/${encodeURIComponent(id)}` : base,
        {
          method: id ? 'PATCH' : 'POST',
          body: draft,
          idempotencyKey: key,
        },
      ),
    onSuccess: () => client.invalidateQueries({ queryKey: ['mkt'] }),
  });
}
