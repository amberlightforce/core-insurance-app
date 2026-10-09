import { useTranslation } from 'react-i18next';

import { Banner } from '../../../design-system';
import { problemOf } from '../../staff/problem';
import file from './reverify.module.css';

/**
 * A failed decision in plain words; the code, trace id and server detail sit under «Technical details»
 * (PITFALLS 27), wrapped, never clipped.
 */
export function ReverifyProblem({ error }: { error: unknown }) {
  const { t, i18n } = useTranslation('claims');
  const problem = problemOf(error);
  const key = problem.code ? `reverify.problems.${problem.code}` : '';
  const message = key && i18n.exists(`claims:${key}`) ? t(key) : t('reverify.problems.generic');
  return (
    <Banner variant="danger" live="alert" title={t('reverify.failed')}>
      <p>{message}</p>
      {problem.code || problem.traceId || problem.detail ? (
        <details className={file.technical}>
          <summary>{t('reverify.technical')}</summary>
          <dl>
            {problem.code ? (
              <>
                <dt>{t('reverify.technicalCode')}</dt>
                <dd className="ds-mono">{problem.code}</dd>
              </>
            ) : null}
            {problem.traceId ? (
              <>
                <dt>{t('reverify.technicalTrace')}</dt>
                <dd className="ds-mono">{problem.traceId}</dd>
              </>
            ) : null}
            {problem.detail ? (
              <>
                <dt>{t('reverify.technicalDetail')}</dt>
                <dd>{problem.detail}</dd>
              </>
            ) : null}
          </dl>
        </details>
      ) : null}
    </Banner>
  );
}
