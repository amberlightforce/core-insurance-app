import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

import { isApiError, type ProblemDetails } from '../../api/client';
import { Banner, Button, type StatusFamily } from '../../design-system';
import styles from './portal.module.css';

/** The page column of a portal screen. */
export function PortalPage({ children }: { children: ReactNode }) {
  return <div className={styles.page}>{children}</div>;
}

export interface PortalHeroProps {
  title: string;
  overline?: string;
  lead?: string;
}

/** The one h1 of a portal screen, on soft sea blooms (the v3 canopy, customer-sized). */
export function PortalHero({ title, overline, lead }: PortalHeroProps) {
  return (
    <header className={styles.hero}>
      <div className={styles.heroImagery} aria-hidden="true">
        <i />
        <i />
        <i />
      </div>
      {overline ? <p className={styles.overline}>{overline}</p> : null}
      <h1 className={styles.title}>{title}</h1>
      {lead ? <p className={styles.lead}>{lead}</p> : null}
    </header>
  );
}

export interface PortalSectionProps {
  title: string;
  family?: StatusFamily;
  children: ReactNode;
}

/** A titled opaque card with the status-family dot before the heading. */
export function PortalSection({ title, family, children }: PortalSectionProps) {
  return (
    <section className={styles.section} aria-label={title}>
      <div className={styles.sectionHeading}>
        {family ? <span className={styles.dot} data-family={family} aria-hidden="true" /> : null}
        <h2 className={styles.sectionTitle}>{title}</h2>
      </div>
      {children}
    </section>
  );
}

function problemOf(error: unknown): ProblemDetails {
  if (isApiError(error)) return error.problem;
  return { status: 0, code: 'UNEXPECTED' };
}

export interface PortalProblemProps {
  error: unknown;
  /** Short context line, e.g. «Η δήλωση δεν υποβλήθηκε». */
  title?: string;
  onRetry?: () => void;
}

/**
 * An API failure in plain words for a customer: 403 is a quiet warning (never a retry), 429 asks to wait, a network
 * failure says so; the trace reference is what the customer quotes to support. No internals are shown.
 */
export function PortalProblem({ error, title, onRetry }: PortalProblemProps) {
  const { t } = useTranslation('portal');
  const problem = problemOf(error);
  const forbidden = problem.status === 403;
  let message = t('problem.generic');
  if (problem.status === 0) message = t('problem.network');
  else if (problem.status === 429) message = t('withdrawal.tooMany');
  else if (forbidden) message = t('states.forbidden');
  const fieldErrors = (problem.errors ?? []).filter((e) => e.field !== '$' && e.message);
  return (
    <Banner
      variant={forbidden ? 'warning' : 'danger'}
      live="alert"
      title={title ?? message}
      actions={
        onRetry && !forbidden ? (
          <Button variant="secondary" size="sm" onPress={onRetry}>
            {t('states.retry')}
          </Button>
        ) : undefined
      }
    >
      {title ? <p>{message}</p> : null}
      {fieldErrors.length > 0 ? (
        <ul className={styles.problemList}>
          {fieldErrors.map((e) => (
            <li key={`${e.field}:${e.code}`}>{e.message}</li>
          ))}
        </ul>
      ) : null}
      {problem.traceId ? (
        <p className="ds-caption">{t('problem.trace', { id: problem.traceId })}</p>
      ) : null}
    </Banner>
  );
}
