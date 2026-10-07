import { useQuery } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type {
  CatalogueGetResponse,
  JobBindRequest,
  JobBindResponse,
  JobQuoteResponse,
  JobUpdateDraftRequest,
  JobUpdateDraftResponse,
  ProductVersionResolveResponse,
  QuestionSetEvaluateResponse,
  QuestionSetGetResponse,
  SubmissionCreateRequest,
  SubmissionCreateResponse,
} from '../../api/types';
import { slice } from './state';

/** pfc.ProductVersion.resolve for the slice product on a date (a read, although it is a POST). */
export function resolveProduct(validAt: string, signal?: AbortSignal) {
  return apiRequest<ProductVersionResolveResponse>('/api/pfc/v1/product-versions/resolve', {
    method: 'POST',
    query: { validAt },
    body: {
      jurisdiction: slice.jurisdiction,
      legalEntity: slice.legalEntity,
      product: slice.product,
      channel: slice.channel,
      transactionType: 'NewBusiness',
    },
    ...(signal ? { signal } : {}),
  });
}

export function useProductVersion(validAt: string) {
  return useQuery({
    queryKey: ['pfc', 'resolve', slice.product, validAt],
    queryFn: ({ signal }) => resolveProduct(validAt, signal),
    staleTime: 60_000,
  });
}

export function useCatalogue(hash: string | undefined) {
  return useQuery({
    queryKey: ['pfc', 'catalogue', hash],
    enabled: hash !== undefined,
    queryFn: ({ signal }) =>
      apiRequest<CatalogueGetResponse>(`/api/pfc/v1/catalogue/${String(hash)}`, { signal }),
    staleTime: Infinity,
  });
}

export function useQuestionSet(hash: string | undefined) {
  return useQuery({
    queryKey: ['pfc', 'question-set', hash, slice.questionSet],
    enabled: hash !== undefined,
    queryFn: ({ signal }) =>
      apiRequest<QuestionSetGetResponse>(`/api/pfc/v1/question-sets/${String(hash)}`, {
        query: { set: slice.questionSet },
        signal,
      }),
    staleTime: Infinity,
  });
}

/** pfc.QuestionSet.evaluate: visibility, missing required answers and knock-outs for the answers so far. */
export function useQuestionEvaluation(
  hash: string | undefined,
  answers: Record<string, string>,
  enabled: boolean,
) {
  return useQuery({
    queryKey: ['pfc', 'question-eval', hash, answers],
    enabled: enabled && hash !== undefined,
    queryFn: ({ signal }) =>
      apiRequest<QuestionSetEvaluateResponse>('/api/pfc/v1/question-sets/evaluate', {
        method: 'POST',
        body: { hash, set: slice.questionSet, answers },
        signal,
      }),
  });
}

/* Commands (pol.yaml): each takes the Idempotency-Key of the user action. */

export function createSubmission(request: SubmissionCreateRequest, key: string) {
  return apiRequest<SubmissionCreateResponse>('/api/pol/v1/submissions', {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

export function updateDraft(request: JobUpdateDraftRequest, key: string) {
  return apiRequest<JobUpdateDraftResponse>('/api/pol/v1/jobs/update-draft', {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

export function quoteJob(request: { jobId: string; versionNo: number }, key: string) {
  return apiRequest<JobQuoteResponse>('/api/pol/v1/jobs/quote', {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

export function bindJob(request: JobBindRequest, key: string) {
  return apiRequest<JobBindResponse>('/api/pol/v1/jobs/bind', {
    method: 'POST',
    body: request,
    idempotencyKey: key,
  });
}

/** pol.Job.get: used to resynchronise the draft version after a POL-ERR-STALE. */
export function getJob(jobId: string) {
  return apiRequest<{
    job: {
      jobId: string;
      currentVersionNo: number;
      versions: { versionNo: number; draftVersion: number }[];
    };
  }>(`/api/pol/v1/jobs/${encodeURIComponent(jobId)}`);
}
