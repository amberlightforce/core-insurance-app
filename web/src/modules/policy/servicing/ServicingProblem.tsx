import { useTranslation } from 'react-i18next';

import { Banner, Button } from '../../../design-system';
import { problemOf } from '../../staff/problem';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { useFormat } from '../../staff/useFormat';
import { explainInputProblem, fieldLabelOf } from '../../quote/inputProblems';

/** Codes with a plain-language explanation under `policy:servicing.problems`. */
const explained = new Set([
  'POL-ERR-EFFDATE-LIMIT',
  'POL-ERR-OUT-OF-SEQUENCE',
  'POL-ERR-PREEMPTED',
  'POL-ERR-AFTER-CANCELLATION',
  'POL-ERR-ILLEGAL-TRANSITION',
  'POL-ERR-REBASE-REQUIRED',
  'POL-ERR-STALE',
  'POL-ERR-HUMAN-CONFIRMATION-REQUIRED',
]);

function extensionText(problem: Record<string, unknown>, ...keys: string[]): string | undefined {
  for (const key of keys) {
    const value = problem[key];
    if (typeof value === 'string' && value) return value;
  }
  return undefined;
}

export interface ServicingProblemProps {
  error: unknown;
  /** What the user was doing («Η δέσμευση της αλλαγής απέτυχε»). */
  title: string;
  /** Takes the user to the step that holds a rating input the engine refused (change workspace only). */
  onGoToVehicle?: () => void;
  onRetry?: () => void;
}

/**
 * A servicing command refusal in plain words (PITFALLS 27): which rule stopped it and what to do next. The code and
 * the trace id stay available under «technical details». A 403 is shown as the no-permission state, and a problem
 * that is not a servicing rule falls back to the generic banner.
 */
export function ServicingProblem({ error, title, onGoToVehicle, onRetry }: ServicingProblemProps) {
  const { t } = useTranslation('policy');
  const quote = useTranslation('quote').t;
  const staff = useTranslation('staff').t;
  const fmt = useFormat();
  const problem = problemOf(error);
  const code = problem.code ?? '';

  if (problem.status === 403) {
    return (
      <Banner variant="warning" live="alert" title={staff('noPermission.title')}>
        {t('servicing.problems.forbidden')}
      </Banner>
    );
  }

  const technical = (
    <details className="ds-caption">
      <summary>{t('servicing.problems.technical')}</summary>
      {problem.code ? <span className="ds-mono">{problem.code}</span> : null}
      {problem.traceId ? (
        <span>{` · ${staff('problem.trace', { id: problem.traceId })}`}</span>
      ) : null}
      {problem.detail ? <p className="ds-mono">{problem.detail}</p> : null}
    </details>
  );

  if (explained.has(code)) {
    const earliest = extensionText(problem, 'earliest', 'earliestAt', 'from');
    const latest = extensionText(problem, 'latest', 'latestAt', 'to');
    return (
      <Banner variant="danger" live="alert" title={title}>
        <p>
          <strong>{t(`servicing.problems.${code}.what`)}</strong>
        </p>
        <p>{t(`servicing.problems.${code}.fix`)}</p>
        {code === 'POL-ERR-EFFDATE-LIMIT' && earliest && latest ? (
          <p>{t('servicing.problems.range', { from: fmt.date(earliest), to: fmt.date(latest) })}</p>
        ) : null}
        {technical}
      </Banner>
    );
  }

  if (
    problem.status === 409 &&
    !problem.code?.startsWith('RAT-') &&
    code !== 'IDEMPOTENCY-REPLAY'
  ) {
    return (
      <Banner variant="danger" live="alert" title={title}>
        <p>
          <strong>{t('servicing.problems.conflict.what')}</strong>
        </p>
        <p>{t('servicing.problems.conflict.fix')}</p>
        {technical}
      </Banner>
    );
  }

  const fix = onGoToVehicle ? explainInputProblem(problem) : null;
  if (fix) {
    const label = fieldLabelOf(fix, quote, {}) ?? '';
    return (
      <Banner
        variant="danger"
        live="alert"
        title={title}
        actions={
          fix.step === 'vehicle' && onGoToVehicle ? (
            <Button variant="secondary" size="sm" onPress={onGoToVehicle}>
              {t('servicing.problems.goToVehicle')}
            </Button>
          ) : undefined
        }
      >
        <p>
          {quote(`inputProblem.fields.${fix.field}.what`, {
            cover: quote('inputProblem.thisCover'),
            label,
          })}
        </p>
        <p>
          {quote(`inputProblem.fields.${fix.field}.fix`, {
            cover: quote('inputProblem.thisCover'),
            label,
          })}
        </p>
        {technical}
      </Banner>
    );
  }

  return <ProblemBanner error={error} title={title} {...(onRetry ? { onRetry } : {})} />;
}
