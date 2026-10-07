import type { CSSProperties } from 'react';
import { Link } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { useRegionFormat } from '../../preferences/context';
import { cx, defined } from '../../utils/cx';
import { Button } from '../Button';
import { formatSigned } from './formatSigned';
import { Popover, type PopoverStatus } from './Popover';
import styles from './Popover.module.css';

export interface ExplainFactor {
  label: string;
  /** Signed contribution; positive bars use `--color-chart-positive`, negative `--color-chart-negative`. */
  value: number;
  /** Display text, e.g. «+1.240,00 €». Defaults to the signed number. */
  display?: string;
}

export interface ExplainSource {
  label: string;
  href?: string;
}

export interface ExplainWhyProps {
  /** The explained figure, already formatted («6.480,00 €»); the title reads «Γιατί 6.480,00 €;». */
  value: string;
  /** Model or rule version line, e.g. «Μοντέλο CLM-RES v4.2 · βεβαιότητα 0,91» or «Πίνακας τιμολόγησης 2026.3». */
  basis?: string;
  factors: ExplainFactor[];
  sources?: ExplainSource[];
  fullSheetHref?: string;
  onViewFullSheet?: () => void;
  /** Trigger text; defaults to «Γιατί;». */
  triggerLabel?: string;
  status?: PopoverStatus;
  onRetry?: () => void;
  isOpen?: boolean;
  defaultOpen?: boolean;
  onOpenChange?: (isOpen: boolean) => void;
}

/**
 * Explain-why popover (IB-08, Part 2 §4.16/§4.37): «Γιατί;» link trigger, the version line, factor rows
 * (label | bar | signed value) whose bars grow with MI-53 while the values are in the DOM from frame 0,
 * numbered sources and «Προβολή πλήρους φύλλου υπολογισμού».
 */
export function ExplainWhy({
  value,
  basis,
  factors,
  sources = [],
  fullSheetHref,
  onViewFullSheet,
  triggerLabel,
  status = 'ready',
  onRetry,
  isOpen,
  defaultOpen,
  onOpenChange,
}: ExplainWhyProps) {
  const { t } = useTranslation('ds');
  const locale = useRegionFormat();
  const title = t('explainWhy.title', { value });
  const max = Math.max(...factors.map((f) => Math.abs(f.value)), 0);
  const showSheet = fullSheetHref !== undefined || onViewFullSheet !== undefined;

  return (
    <Popover
      trigger={
        <Button variant="link" aria-label={title}>
          {triggerLabel ?? t('explainWhy.trigger')}
        </Button>
      }
      title={title}
      size="md"
      status={status}
      {...defined({ onRetry, isOpen, defaultOpen, onOpenChange })}
      footer={
        showSheet ? (
          <Link
            className={cx(styles.textLink)}
            {...defined({ href: fullSheetHref, onPress: onViewFullSheet })}
          >
            {t('explainWhy.fullSheet')}
          </Link>
        ) : undefined
      }
    >
      {basis ? <p className={cx(styles.basis)}>{basis}</p> : null}
      <ul className={cx(styles.factors)} aria-label={t('explainWhy.factors')}>
        {factors.map((factor, index) => {
          const share = max > 0 ? Math.abs(factor.value) / max : 0;
          const style = { '--_share': String(share), '--_i': String(index) } as CSSProperties;
          return (
            <li key={factor.label} className={cx(styles.factor)}>
              <span className={cx(styles.factorLabel)}>{factor.label}</span>
              <span className={cx(styles.factorTrack)} aria-hidden="true">
                <span
                  className={cx(styles.factorBar)}
                  data-sign={factor.value < 0 ? 'negative' : 'positive'}
                  style={style}
                />
              </span>
              <span className={cx(styles.factorValue, 'ds-num')}>
                {factor.display ?? formatSigned(factor.value, locale)}
              </span>
            </li>
          );
        })}
      </ul>
      {sources.length > 0 ? (
        <div className={cx(styles.sources)}>
          <p className={cx(styles.sourcesTitle)}>{t('explainWhy.sources')}</p>
          <ol className={cx(styles.sourceList)}>
            {sources.map((source) => (
              <li key={source.label}>
                {source.href ? (
                  <Link className={cx(styles.textLink)} href={source.href}>
                    {source.label}
                  </Link>
                ) : (
                  source.label
                )}
              </li>
            ))}
          </ol>
        </div>
      ) : null}
    </Popover>
  );
}
