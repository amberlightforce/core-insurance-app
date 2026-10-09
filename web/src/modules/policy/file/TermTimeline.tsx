import { Check } from 'lucide-react';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../../design-system/icons';
import { useFormat } from '../../staff/useFormat';
import type { TermViewModel } from './api';
import { stripState } from './model';
import styles from './PolicyFile.module.css';

const ended = new Set(['CANCELLED', 'EXPIRED', 'VOIDED', 'REWRITTEN']);

/**
 * The policy file's term timeline (the claim file's stage strip): term 1 … n joined by tide-gradient
 * connectors, the viewed term current, earlier terms with a check, later (renewal) terms hollow. Each term states
 * its own state in words, so colour is never the only signal.
 */
export function TermTimeline({
  terms,
  viewed,
}: {
  terms: readonly TermViewModel[];
  viewed: TermViewModel;
}) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const currentIndex = Math.max(
    0,
    terms.findIndex((x) => x.termId === viewed.termId),
  );
  return (
    <ol className={styles.strip} aria-label={t('file.timeline.label')}>
      {terms.map((term, index) => {
        const state = stripState(term, viewed);
        const isRenewal = term.termNumber > 1 && term.termNumber > viewed.termNumber;
        return (
          <li
            key={term.termId}
            className={styles.stage}
            data-state={state}
            data-ended={ended.has(term.state) || undefined}
            {...(state === 'current' ? { 'aria-current': 'step' as const } : {})}
          >
            {index > 0 ? (
              <span
                className={styles.connector}
                data-filled={index <= currentIndex || undefined}
                aria-hidden="true"
              />
            ) : null}
            <span className={styles.node} aria-hidden="true">
              {state === 'done' ? <Icon icon={Check} size={12} /> : null}
            </span>
            <span className={styles.stageLabel}>
              {t('file.timeline.term', { number: term.termNumber })}
              {isRenewal ? ` · ${t('file.timeline.renewal')}` : ''}
            </span>
            <span className={styles.stageDates}>
              {fmt.date(term.period.from)} – {term.period.to ? fmt.date(term.period.to) : '…'}
            </span>
            <span>{t(`file.state.${term.state}`, { defaultValue: term.state })}</span>
          </li>
        );
      })}
    </ol>
  );
}
