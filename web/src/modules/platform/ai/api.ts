import { useMutation, useQueries, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components as Plt } from '../../../api/generated/plt';

type Schemas = Plt['schemas'];
export type AiToggleState = Schemas['AiToggleResolveResponse'];
export type AiToggleSetRequest = Schemas['AiToggleSetRequest'];
export type AiKillSwitchRequest = Schemas['AiKillSwitchRequest'];

const plt = '/api/plt/v1';

/**
 * The AI features the control plane knows. plt.yaml has no list operation yet, so the screen starts from this
 * catalogue (PRD AI register) and the user may add a feature id; every row is then resolved against the server.
 * TODO(hardening): replace by a plt.Ai.list read once the backend exposes one.
 */
export const knownFeatures: readonly string[] = ['AI-CLM-01', 'AI-CLM-04', 'AI-BIL-SUSPENSE'];
export const featureIdPattern = /^AI-[A-Z]{2,3}-\d{2,}$/;
/** GLOBAL is the kill-switch scope that stops every AI feature. */
export const globalScope = 'GLOBAL';

/** plt.AiToggle.resolve for every feature: the effective state the gate would answer now. */
export function useFeatureStates(features: readonly string[]) {
  return useQueries({
    queries: features.map((featureId) => ({
      queryKey: ['plt', 'ai', 'state', featureId],
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        apiRequest<AiToggleState>(`${plt}/ai-toggle/resolve`, {
          method: 'POST',
          body: { featureId },
          signal,
        }),
    })),
  });
}

/** plt.AiToggle.set: enabling needs an APPROVED register entry (server rule); disabling is always allowed. */
export function useSetToggle() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (input: { request: AiToggleSetRequest; key: string }) =>
      apiRequest<Schemas['AiToggleSetResponse']>(`${plt}/ai-toggle/set`, {
        method: 'POST',
        body: input.request,
        idempotencyKey: input.key,
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['plt', 'ai', 'state'] }),
  });
}

/** plt.Ai.killSwitch: activate or clear the GLOBAL (or one feature's) kill switch. */
export function useKillSwitch() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (input: { request: AiKillSwitchRequest; key: string }) =>
      apiRequest<Schemas['AiKillSwitchResponse']>(`${plt}/ai/kill-switch`, {
        method: 'POST',
        body: input.request,
        idempotencyKey: input.key,
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['plt', 'ai', 'state'] }),
  });
}
