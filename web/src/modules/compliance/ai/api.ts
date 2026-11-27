import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components as Cmp } from '../../../api/generated/cmp';

/**
 * AI-system register (NX-UI-11): cmp.AiSystem.list / register / submitForReview / approve / suspend / retire.
 * The server decides every move (allowed source states, who may approve); the screen only shows its refusal.
 */
export type AiSystem = Cmp['schemas']['AiSystemListItem'];

export type AiAction = 'submit-for-review' | 'approve' | 'suspend' | 'retire';

const root = '/api/cmp/v1/ai-systems';

export const featureIdPattern = /^AI-[A-Z]{2,3}-\d{2,}$/;

export function useAiSystems() {
  return useQuery({
    queryKey: ['cmp', 'ai-systems'],
    queryFn: ({ signal }) =>
      apiRequest<{ items: AiSystem[]; nextCursor?: string | null }>(root, {
        query: { limit: 200 },
        signal,
      }),
  });
}

/** The moves the register accepts from each status (mirrors the server's AiMoveHandler sources). */
export function actionsFor(status: AiSystem['status']): AiAction[] {
  switch (status) {
    case 'DRAFT':
      return ['submit-for-review', 'retire'];
    case 'IN_REVIEW':
      return ['approve', 'retire'];
    case 'APPROVED':
      return ['suspend', 'retire'];
    case 'SUSPENDED':
      return ['submit-for-review', 'retire'];
    default:
      return [];
  }
}

export function useRegisterAiSystem() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({
      body,
      key,
    }: {
      body: { featureId: string; name: string; aiActClass?: string };
      key: string;
    }) => apiRequest<AiSystem>(`${root}/register`, { method: 'POST', body, idempotencyKey: key }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['cmp', 'ai-systems'] }),
  });
}

export function useAiAction() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({
      action,
      featureId,
      reason,
      key,
    }: {
      action: AiAction;
      featureId: string;
      reason: string;
      key: string;
    }) =>
      apiRequest<AiSystem>(`${root}/${action}`, {
        method: 'POST',
        body: { featureId, ...(reason ? { reason } : {}) },
        idempotencyKey: key,
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['cmp', 'ai-systems'] }),
  });
}
