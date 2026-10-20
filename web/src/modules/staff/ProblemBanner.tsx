import { useTranslation } from 'react-i18next';

import { readSession } from '../../dev-auth/devAuth';
import { Banner, Button } from '../../design-system';
import { problemOf } from './problem';
import styles from './staff.module.css';

/** Problem code → i18n key under `staff:problem.codes` (codes contain hyphens only, which i18next keys allow). */
function codeKey(code: string | undefined): string | null {
  return code && /^[A-Z0-9-]+$/.test(code) ? `problem.codes.${code}` : null;
}

export interface ProblemBannerProps {
  error: unknown;
  /** Short context line, e.g. «Η δέσμευση απέτυχε». */
  title?: string;
  onRetry?: () => void;
  retryLabel?: string;
}

/**
 * Shows an RFC 9457 Problem Details response readably: a localised headline for known codes (else the server's
 * title), the server's detail, per-field errors, and the code with the trace id for support. The trace id is
 * not personal data (D-SLC-05) and is what an operator quotes when reporting a failure.
 * A 403 is the `forbidden` variant: a warning «No permission» (never a retry), with the context line kept below it.
 */
export function ProblemBanner({ error, title, onRetry, retryLabel }: ProblemBannerProps) {
  const { t, i18n } = useTranslation('staff');
  const problem = problemOf(error);
  const key = codeKey(problem.code);
  const known = key && i18n.exists(`staff:${key}`) ? t(key) : null;
  // A 403 carries no body: say plainly that the signed-in role may not do this, instead of a bare «failed».
  const forbidden = problem.status === 403;
  const headline =
    (forbidden ? t('noPermission.title') : null) ??
    title ??
    known ??
    problem.title ??
    (problem.status === 0 ? t('problem.network') : t('problem.generic'));
  const roles = readSession()?.user.roles.join(', ') ?? '';
  const fieldErrors = (problem.errors ?? []).filter((e) => e.field !== '$');
  const detail = problem.detail;

  return (
    <Banner
      variant={forbidden ? 'warning' : 'danger'}
      live="alert"
      title={headline}
      actions={
        onRetry && !forbidden ? (
          <Button variant="secondary" size="sm" onPress={onRetry}>
            {retryLabel ?? t('retry')}
          </Button>
        ) : undefined
      }
    >
      {forbidden && title ? <p>{title}</p> : null}
      {known && title && !forbidden ? <p>{known}</p> : null}
      {forbidden && !problem.detail ? (
        <p>{roles ? t('noPermission.action', { roles }) : t('noPermission.actionUnknown')}</p>
      ) : null}
      {detail && detail !== headline ? <p>{detail}</p> : null}
      {fieldErrors.length > 0 ? (
        <ul className={styles.problemList}>
          {fieldErrors.map((e) => (
            <li key={`${e.field}:${e.code}`}>
              <span className="ds-mono">{e.field}</span>: {e.message ?? e.code}
            </li>
          ))}
        </ul>
      ) : null}
      {problem.code || problem.traceId ? (
        <p className="ds-caption">
          {problem.code ? <span className="ds-mono">{problem.code}</span> : null}
          {problem.code && problem.traceId ? ' · ' : null}
          {problem.traceId ? t('problem.trace', { id: problem.traceId }) : null}
        </p>
      ) : null}
    </Banner>
  );
}
