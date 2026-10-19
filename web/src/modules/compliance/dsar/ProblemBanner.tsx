import { useTranslation } from 'react-i18next';

import { Banner } from '../../../design-system';
import { problemOf } from '../../staff/problem';

/** A failed command: the server's own explanation (the CMP error code, the Problem detail) with the trace id. */
export function ProblemBanner({ error }: { error: unknown }) {
  const { t } = useTranslation('compliance');
  const problem = problemOf(error);
  const message =
    problem.status === 403
      ? t('dsar.errors.forbidden')
      : (problem.detail ?? problem.title ?? t('dsar.errors.generic'));
  return (
    <Banner variant="danger" live="alert" title={t('dsar.errors.title')}>
      <p>{message}</p>
      {problem.code || problem.traceId ? (
        <p className="ds-caption">
          {problem.code ? <span className="ds-mono">{problem.code}</span> : null}
          {problem.code && problem.traceId ? ' · ' : null}
          {problem.traceId ? t('dsar.errors.trace', { id: problem.traceId }) : null}
        </p>
      ) : null}
    </Banner>
  );
}
