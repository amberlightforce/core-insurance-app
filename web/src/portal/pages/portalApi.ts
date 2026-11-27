import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type { components } from '../../api/generated/chn';
import { apiFetch } from '../../dev-auth/devAuth';

/** Typed access to the customer portal API (BO-CHN-02/03/04). Every path is `/api/chn/v1`, role `Customer.Portal`. */
type S = components['schemas'];
export type Money = S['Money'];
export type PortalDashboard = S['PortalDashboardResponse'];
export type PortalRenewalDue = S['PortalRenewalDue'];
export type PortalPolicyListItem = S['PortalPolicyListItem'];
export type PortalPolicy = S['PortalPolicyDetailResponse'];
export type PortalBillingSummary = S['PortalBillingSummary'];
export type PortalInvoice = S['PortalInvoice'];
export type PortalRefund = S['PortalRefund'];
export type PortalPayNow = S['PortalPayNowResponse'];
export type PortalDocument = S['PortalDocument'];
export type NotificationItem = S['NotificationListItem'];
export type NotificationClass = S['NotificationSubscriptionItem']['notificationClass'];
export type MessageThread = S['MessageThread'];
export type MessageTopic = NonNullable<S['MessageSendRequest']['topic']>;
export type ChangePreview = S['ChangePreviewResponse'];
export type ChangeInstruction = S['ChangeInstruction'];
export type PortalContact = S['PortalContactView'];
export type ContactUpdate = S['PortalContactUpdateRequest'];
export type RenewalDeclineReason = S['PortalRenewalDeclineRequest']['reasonCode'];
export type CancellationReason = S['PortalCancellationSubmitRequest']['reasonCode'];

const base = '/api/chn/v1';

export const portalKeys = {
  all: ['portal'] as const,
  dashboard: ['portal', 'dashboard'] as const,
  policies: ['portal', 'policies'] as const,
  policy: (id: string) => ['portal', 'policy', id] as const,
  billing: ['portal', 'billing'] as const,
  documents: ['portal', 'documents'] as const,
  notifications: ['portal', 'notifications'] as const,
  subscriptions: ['portal', 'subscriptions'] as const,
  messages: ['portal', 'messages'] as const,
  contact: ['portal', 'contact'] as const,
};

export interface Page<T> {
  items: T[];
  nextCursor: string | null;
  unreadCount?: number;
}

export function getDashboard() {
  return apiRequest<PortalDashboard>(`${base}/portal/dashboard`);
}

/** The dashboard feeds the shell's bell, so the shell and the overview share one cache entry. */
export function usePortalDashboard(enabled = true) {
  return useQuery({ queryKey: portalKeys.dashboard, queryFn: getDashboard, enabled });
}

export function listPolicies(cursor?: string) {
  return apiRequest<Page<PortalPolicyListItem>>(`${base}/portal/policies`, {
    query: { cursor, limit: 50 },
  });
}

export function getPolicy(id: string) {
  return apiRequest<PortalPolicy>(`${base}/portal/policies/${encodeURIComponent(id)}`);
}

export function getBillingSummary() {
  return apiRequest<PortalBillingSummary>(`${base}/portal/billing/summary`);
}

export function listInvoices(cursor?: string) {
  return apiRequest<Page<PortalInvoice>>(`${base}/portal/billing/invoices`, {
    query: { cursor, limit: 50 },
  });
}

export function listRefunds() {
  return apiRequest<{ items: PortalRefund[] }>(`${base}/portal/billing/refunds`);
}

export function payNow(invoiceId: string, idempotencyKey: string) {
  return apiRequest<PortalPayNow>(
    `${base}/portal/billing/invoices/${encodeURIComponent(invoiceId)}/pay-now`,
    { method: 'POST', body: {}, idempotencyKey },
  );
}

export function listDocuments(cursor?: string, policyId?: string) {
  return apiRequest<Page<PortalDocument>>(`${base}/portal/documents`, {
    query: { cursor, limit: 50, policyId },
  });
}

/** Fetches the document bytes with the bearer token (the response streams the file). */
export async function downloadDocument(id: string): Promise<{ blob: Blob; sha256: string | null }> {
  const response = await apiFetch(`${base}/portal/documents/${encodeURIComponent(id)}/download`);
  if (!response.ok) throw new Error(`HTTP ${String(response.status)}`);
  return { blob: await response.blob(), sha256: response.headers.get('X-Content-SHA256') };
}

export function listNotifications() {
  return apiRequest<Page<NotificationItem>>(`${base}/notifications`, { query: { limit: 100 } });
}

export function markNotificationsRead(
  body: { notificationIds: string[] } | { all: true },
  idempotencyKey: string,
) {
  return apiRequest<S['NotificationMarkReadResponse']>(`${base}/notifications/mark-read`, {
    method: 'POST',
    body,
    idempotencyKey,
  });
}

export function listSubscriptions() {
  return apiRequest<S['NotificationSubscriptionListResponse']>(
    `${base}/notification-subscriptions`,
  );
}

export function setSubscription(
  notificationClass: NotificationClass,
  enabled: boolean,
  idempotencyKey: string,
) {
  return apiRequest<S['NotificationSubscriptionSetResponse']>(
    `${base}/notification-subscriptions/set`,
    { method: 'POST', body: { notificationClass, enabled }, idempotencyKey },
  );
}

export function listMessages() {
  return apiRequest<Page<{ thread: MessageThread }>>(`${base}/messages`, { query: { limit: 50 } });
}

export function sendMessage(body: S['MessageSendRequest'], idempotencyKey: string) {
  return apiRequest<S['MessageSendResponse']>(`${base}/messages/send`, {
    method: 'POST',
    body,
    idempotencyKey,
  });
}

export function previewChange(body: S['ChangePreviewRequest'], idempotencyKey: string) {
  return apiRequest<ChangePreview>(`${base}/changes/preview`, {
    method: 'POST',
    body,
    idempotencyKey,
  });
}

export function confirmChange(changeId: string, idempotencyKey: string) {
  return apiRequest<S['ChangeConfirmResponse']>(`${base}/changes/confirm`, {
    method: 'POST',
    body: { changeId, acknowledged: true },
    idempotencyKey,
  });
}

export function referChange(changeId: string, idempotencyKey: string) {
  return apiRequest<S['ChangeReferResponse']>(`${base}/changes/refer`, {
    method: 'POST',
    body: { changeId },
    idempotencyKey,
  });
}

export function acceptRenewal(policyId: string, jobId: string, idempotencyKey: string) {
  return apiRequest<S['PortalRenewalAcceptResponse']>(`${base}/portal/renewals/accept`, {
    method: 'POST',
    body: { policyId, jobId, acknowledged: true },
    idempotencyKey,
  });
}

export function declineRenewal(
  policyId: string,
  jobId: string,
  reasonCode: RenewalDeclineReason,
  idempotencyKey: string,
) {
  return apiRequest<S['PortalRenewalDeclineResponse']>(`${base}/portal/renewals/decline`, {
    method: 'POST',
    body: { policyId, jobId, reasonCode },
    idempotencyKey,
  });
}

export function requestCancellation(
  policyId: string,
  reasonCode: CancellationReason,
  effectiveDate: string | undefined,
  idempotencyKey: string,
) {
  return apiRequest<S['PortalCancellationSubmitResponse']>(`${base}/portal/cancellations/request`, {
    method: 'POST',
    body: { policyId, reasonCode, ...(effectiveDate ? { effectiveDate } : {}) },
    idempotencyKey,
  });
}

export function getContact() {
  return apiRequest<PortalContact>(`${base}/portal/contact`);
}

export function updateContact(body: ContactUpdate, idempotencyKey: string) {
  return apiRequest<S['PortalContactUpdateResponse']>(`${base}/portal/contact/update`, {
    method: 'POST',
    body,
    idempotencyKey,
  });
}

/** A command that changes portal data: on success every portal read is refetched (counts, balances, lists). */
export function useInvalidatingMutation<TData, TVars>(fn: (vars: TVars) => Promise<TData>) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => client.invalidateQueries({ queryKey: portalKeys.all }),
  });
}
