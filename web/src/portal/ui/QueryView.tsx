import type { UseQueryResult } from '@tanstack/react-query';
import type { ReactNode } from 'react';

import { LoadingState, SkeletonText } from '../../design-system';
import { PortalProblem } from './PortalUi';

export interface QueryViewProps<T> {
  query: UseQueryResult<T>;
  children: (data: T) => ReactNode;
  /** Context line above the plain-language reason when the load fails. */
  errorTitle?: string;
}

/**
 * The three states every portal read has: loading skeleton, a plain-language failure with a retry (403 is quiet,
 * never retried), then the data. Empty lists are the caller's job (it knows what "empty" means).
 */
export function QueryView<T>({ query, children, errorTitle }: QueryViewProps<T>) {
  if (query.isPending) {
    return (
      <LoadingState immediate>
        <SkeletonText lines={3} />
      </LoadingState>
    );
  }
  if (query.isError) {
    return (
      <PortalProblem
        error={query.error}
        {...(errorTitle ? { title: errorTitle } : {})}
        onRetry={() => {
          void query.refetch();
        }}
      />
    );
  }
  return <>{children(query.data)}</>;
}
