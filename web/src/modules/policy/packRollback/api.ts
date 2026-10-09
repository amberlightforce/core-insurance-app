import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import { useDevSession } from '../../../dev-auth/devAuth';
import type {
  PackRollbackExceptionListPage,
  PackRollbackExceptionReviewRequest,
  PackRollbackExceptionView,
} from '../../../api/types';

const pol = '/api/pol/v1';

export type ExceptionFilter = 'OPEN' | 'REVIEWED';

/** pol.PackRollbackException.list, filtered by status. Only the status travels in the query. */
export function useExceptions(status: ExceptionFilter) {
  return useQuery({
    queryKey: ['pol', 'packRollbackExceptions', status],
    queryFn: ({ signal }) =>
      apiRequest<PackRollbackExceptionListPage>(`${pol}/pack-rollback-exceptions`, {
        query: { status },
        signal,
      }),
  });
}

/** pol.PackRollbackException.get (by exception id). */
export function useException(exceptionId: string) {
  return useQuery({
    queryKey: ['pol', 'packRollbackException', exceptionId],
    queryFn: ({ signal }) =>
      apiRequest<PackRollbackExceptionView>(
        `${pol}/pack-rollback-exceptions/${encodeURIComponent(exceptionId)}`,
        { signal },
      ),
  });
}

/** pol.PackRollbackException.review: records the outcome with a reason. Nothing is re-rated (D-SL3-02). */
export function useReviewException() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ body, key }: { body: PackRollbackExceptionReviewRequest; key: string }) =>
      apiRequest<PackRollbackExceptionView>(`${pol}/pack-rollback-exceptions/review`, {
        method: 'POST',
        body,
        idempotencyKey: key,
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['pol'] }),
  });
}

/** Roles that review pack-rollback exceptions (illustrative; the API enforces pol.PackRollbackException.*). */
const reviewerRoles = ['Staff.UnderwritingManager', 'Platform.Admin'];

export function useCanReview(): boolean {
  const roles = useDevSession()?.user.roles ?? [];
  return roles.some((r) => reviewerRoles.includes(r));
}
