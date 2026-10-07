import { ChevronRight, CircleAlert, Home, RotateCcw } from 'lucide-react';
import type { ReactNode } from 'react';
import { Button as AriaButton, Disclosure, DisclosurePanel, Link } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import { Heading, type HeadingLevel } from './Heading';
import { IllCrackedHouse } from './illustrations';
import styles from './States.module.css';

export interface ErrorStateProps {
  /**
   * `region`: an inline banner in the affected region only; the rest of the page keeps working.
   * `page`: a full-sheet state for a 500 or a render failure.
   */
  scope?: 'region' | 'page';
  /** What happened, where and how to fix it (Part 4 §8.4). Defaults to a generic load failure. */
  message?: string;
  onRetry?: () => void;
  /** Correlation / trace id, shown inside «Λεπτομέρειες» (never as the message). */
  correlationId?: string;
  /** Page scope: home route for «Επιστροφή στην αρχική». */
  homeHref?: string;
  /** Page scope: opens the problem report pre-filled with the trace id. */
  onReport?: (correlationId: string | undefined) => void;
  /** Page scope: replace the default reassurance sentence (unsaved input is preserved as a draft). */
  description?: string;
  headingLevel?: HeadingLevel;
  /** Extra actions in the banner. */
  actions?: ReactNode;
}

function Details({ id }: { id: string }) {
  const { t } = useTranslation('ds');
  return (
    <Disclosure className={cx(styles.disclosure)}>
      <AriaButton slot="trigger" className={cx(styles.disclosureTrigger)}>
        <span className={styles.chevron}>
          <Icon icon={ChevronRight} size={12} />
        </span>
        {t('errorState.details')}
      </AriaButton>
      <DisclosurePanel>
        <span className={styles.code}>{t('errorState.code', { id })}</span>
      </DisclosurePanel>
    </Disclosure>
  );
}

/** Recoverable (region) and page-level errors (DESIGN-B A.11). No retry loops: retry is always a user action. */
export function ErrorState({
  scope = 'region',
  message,
  onRetry,
  correlationId,
  homeHref,
  onReport,
  description,
  headingLevel = 1,
  actions,
}: ErrorStateProps) {
  const { t } = useTranslation('ds');

  if (scope === 'page') {
    return (
      <div className={styles.page} role="alert">
        <IllCrackedHouse size="lg" />
        <Heading level={headingLevel} className={styles.pageTitle}>
          {message ?? t('errorState.pageTitle')}
        </Heading>
        <p className={styles.description}>{description ?? t('errorState.pageBody')}</p>
        <div className={styles.actions}>
          {homeHref ? (
            <Link className={cx(styles.link)} href={homeHref}>
              <Icon icon={Home} size={14} /> {t('errorState.home')}
            </Link>
          ) : null}
          {onReport ? (
            <Button
              variant="secondary"
              onPress={() => {
                onReport(correlationId);
              }}
            >
              {t('errorState.report')}
            </Button>
          ) : null}
          {actions}
        </div>
        {correlationId ? <Details id={correlationId} /> : null}
      </div>
    );
  }

  return (
    <div className={styles.banner} role="alert">
      <span className={styles.bannerIcon}>
        <Icon icon={CircleAlert} size={16} />
      </span>
      <div className={styles.bannerBody}>
        <span>{message ?? t('errorState.regionMessage')}</span>
        {onRetry || actions ? (
          <div className={styles.bannerActions}>
            {onRetry ? (
              <Button variant="secondary" size="sm" icon={RotateCcw} onPress={onRetry}>
                {t('errorState.retry')}
              </Button>
            ) : null}
            {actions}
          </div>
        ) : null}
        {correlationId ? <Details id={correlationId} /> : null}
      </div>
    </div>
  );
}
