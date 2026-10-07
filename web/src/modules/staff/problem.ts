import { isApiError, type ProblemDetails } from '../../api/client';

/** The Problem Details of a failure: the API's own, or a synthesised one for unexpected errors. */
export function problemOf(error: unknown): ProblemDetails {
  if (isApiError(error)) return error.problem;
  return {
    status: 0,
    code: 'UNEXPECTED',
    title: error instanceof Error ? error.message : undefined,
  } as ProblemDetails;
}
