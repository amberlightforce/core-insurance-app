import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

import { Banner, ErrorState, LoadingState } from '../../design-system';
import { problemOf } from './problem';

export interface QueryViewProps<T> {
  query: {
    data: T | undefined;
    isPending: boolean;
    isError: boolean;
    error: unknown;
    refetch: () => unknown;
  };
  /** A 404 is shown as «not found» rather than a generic failure. */
  notFoundMessage?: string;
  children: (data: T) => ReactNode;
}

/** Loading, error (with retry and the trace id) and ready states of one query, in one place. */
export function QueryView<T>({ query, notFoundMessage, children }: QueryViewProps<T>) {
  const { t } = useTranslation('staff');
  if (query.isPending) {
    return <LoadingState immediate>{t('loading')}</LoadingState>;
  }
  if (query.isError) {
    const problem = problemOf(query.error);
    if (problem.status === 403) {
      return (
        <Banner variant="warning" title={t('noPermission.title')}>
          {t('noPermission.body')}
        </Banner>
      );
    }
    const message =
      problem.status === 404 && notFoundMessage
        ? notFoundMessage
        : (problem.title ?? t('problem.generic'));
    return (
      <ErrorState
        message={message}
        {...(problem.detail ? { description: problem.detail } : {})}
        {...(problem.traceId ? { correlationId: problem.traceId } : {})}
        {...(problem.status === 404
          ? {}
          : {
              onRetry: () => {
                void query.refetch();
              },
            })}
      />
    );
  }
  return <>{children(query.data as T)}</>;
}
