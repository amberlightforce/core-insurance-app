import { apiRequest } from '../../../api/client';
import type { components } from '../../../api/generated/pol';
import type { JobBindRequest } from '../../../api/types';

type Schemas = components['schemas'];

/** Typed straight from the generated POL contract (SL3-CONTRACTS); the shared `api/types.ts` is not touched. */
export type ServicingPreview = Schemas['ServicingPreview'];
export type ServicingProratedLine = Schemas['ServicingProratedLine'];
export type ServicingTaxLine = Schemas['ServicingTaxLine'];
export type PolicyChangeCreateRequest = Schemas['PolicyChangeCreateRequest'];
export type PolicyChangeCreateResponse = Schemas['PolicyChangeCreateResponse'];
export type CancellationCreateRequest = Schemas['CancellationCreateRequest'];
export type CancellationCreateResponse = Schemas['CancellationCreateResponse'];
export type RenewalCreateResponse = Schemas['RenewalCreateResponse'];
export type RenewalOfferResponse = Schemas['RenewalOfferResponse'];
export type RenewalAcceptRequest = Schemas['RenewalAcceptRequest'];
export type RenewalAcceptResponse = Schemas['RenewalAcceptResponse'];
export type TermTimelineResponse = Schemas['TermTimelineResponse'];
export type JobQuote = Schemas['JobQuoteResponse'];
export type JobBind = Schemas['JobBindResponse'];
export type JobUpdateDraft = Schemas['JobUpdateDraftResponse'];
export type JobUpdateDraftRequest = Schemas['JobUpdateDraftRequest'];
export type DraftInstruction = Schemas['DraftInstruction'];
export type Vehicle = Schemas['Vehicle'];
export type CoverageSelection = Schemas['CoverageSelection'];
export type CancellationKind = Schemas['CancellationKind'];

/* Commands: each takes the Idempotency-Key of the user action (reused on retry). */

export function createPolicyChange(request: PolicyChangeCreateRequest, key: string) {
  return apiRequest<PolicyChangeCreateResponse>('/api/pol/v1/policy-changes', {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

/** Cancellation is one atomic command; dryRun previews it without creating a job or changing the policy. */
export function createCancellation(
  request: CancellationCreateRequest,
  key: string,
  dryRun = false,
) {
  return apiRequest<CancellationCreateResponse>('/api/pol/v1/cancellations', {
    method: 'POST',
    body: request,
    idempotencyKey: key,
    ...(dryRun ? { query: { dryRun: true } } : {}),
  });
}

export function createRenewal(request: { termId: string; reason?: string }, key: string) {
  return apiRequest<RenewalCreateResponse>('/api/pol/v1/renewals', {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

export function offerRenewal(request: { jobId: string; termId: string }, key: string) {
  return apiRequest<RenewalOfferResponse>('/api/pol/v1/renewals/offer', {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

export function acceptRenewal(request: RenewalAcceptRequest, key: string) {
  return apiRequest<RenewalAcceptResponse>('/api/pol/v1/renewals/accept', {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

export function updateServicingDraft(request: JobUpdateDraftRequest, key: string) {
  return apiRequest<JobUpdateDraft>('/api/pol/v1/jobs/update-draft', {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

export function quoteServicingJob(request: { jobId: string; versionNo: number }, key: string) {
  return apiRequest<JobQuote>('/api/pol/v1/jobs/quote', {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

export function bindServicingJob(request: JobBindRequest, key: string) {
  return apiRequest<JobBind>('/api/pol/v1/jobs/bind', {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

/** pol.Job.get: the current quote version of a job (a renewal job is created without one in the response). */
export function getServicingJob(jobId: string) {
  return apiRequest<{
    job: {
      jobId: string;
      currentVersionNo: number;
      versions: { versionNo: number; draftVersion: number }[];
    };
  }>(`/api/pol/v1/jobs/${encodeURIComponent(jobId)}`);
}
