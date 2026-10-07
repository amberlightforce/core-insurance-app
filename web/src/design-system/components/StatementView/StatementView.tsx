import { ArrowDownRight } from 'lucide-react';
import { useId, type ReactNode } from 'react';
import { Link } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx, defined } from '../../utils/cx';
import { formatDate } from '../../../format/dates';
import { formatMoney } from '../../../format/numbers';
import { ErrorState, SkeletonBlock } from '../States';
import { Heading, type HeadingLevel } from '../States/Heading';
import styles from './StatementView.module.css';

export type StatementDirection = 'receivable' | 'payable' | 'settled';

/**
 * Which movements are adverse for this statement type. The colour follows this rule, never the sign alone:
 * on a commission statement a negative line (a clawback) is adverse; on a claim payments statement a
 * positive line (an outgoing payment) may be the one the reader must notice.
 */
export type AdverseRule = 'negative' | 'positive' | 'none';

export interface StatementLine {
  id: string;
  description: string;
  amount: number;
  date?: Date;
  reference?: string;
  /** Subtotals get a 1 px top rule and are excluded from the running balance. */
  kind?: 'line' | 'subtotal';
  /** Per-line override of the statement-type rule. */
  isAdverse?: boolean;
  href?: string;
  onPress?: () => void;
}

export interface StatementViewProps {
  title?: string;
  headingLevel?: HeadingLevel;
  /** Headline balance; shown as an absolute amount with the direction label carrying the meaning. */
  balance: number;
  direction: StatementDirection;
  currency?: string;
  /** Period selector slot (a Select or segmented control from the caller). */
  periodSelector?: ReactNode;
  lines: StatementLine[];
  adverseRule: AdverseRule;
  showRunningBalance?: boolean;
  openingBalance?: number;
  state?: 'ready' | 'loading' | 'empty' | 'error';
  errorMessage?: string;
  onRetry?: () => void;
  correlationId?: string;
}

function isAdverseLine(line: StatementLine, rule: AdverseRule): boolean {
  if (line.isAdverse !== undefined) return line.isAdverse;
  if (rule === 'negative') return line.amount < 0;
  if (rule === 'positive') return line.amount > 0;
  return false;
}

/** Statement view (Part 2 §4.28): headline balance with direction, period slot, line items, subtotals. */
export function StatementView({
  title,
  headingLevel = 3,
  balance,
  direction,
  currency = 'EUR',
  periodSelector,
  lines,
  adverseRule,
  showRunningBalance = false,
  openingBalance = 0,
  state = 'ready',
  errorMessage,
  onRetry,
  correlationId,
}: StatementViewProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const titleId = useId();
  const listLabel = t('statementView.lines');

  const directionLabel =
    direction === 'receivable'
      ? t('statementView.receivable')
      : direction === 'payable'
        ? t('statementView.payable')
        : t('statementView.settled');

  const rows = lines.reduce<{ line: StatementLine; running: number }[]>((acc, line) => {
    const previous = acc[acc.length - 1]?.running ?? openingBalance;
    acc.push({ line, running: line.kind === 'subtotal' ? previous : previous + line.amount });
    return acc;
  }, []);

  let content: ReactNode;
  if (state === 'loading') {
    content = (
      <div className={styles.loading} aria-busy="true">
        <span className="ds-visually-hidden">{t('statementView.loading')}</span>
        {[0, 1, 2, 3].map((i) => (
          <div key={i} className={styles.skeletonRow}>
            <SkeletonBlock width="56%" />
            <SkeletonBlock width="72px" />
          </div>
        ))}
      </div>
    );
  } else if (state === 'error') {
    content = <ErrorState {...defined({ message: errorMessage, onRetry, correlationId })} />;
  } else if (state === 'empty' || lines.length === 0) {
    content = (
      <p className={styles.empty} role="status">
        {t('statementView.empty')}
      </p>
    );
  } else {
    content = (
      <ul
        className={styles.lines}
        aria-label={listLabel}
        data-running={showRunningBalance || undefined}
      >
        {rows.map(({ line, running: after }) => {
          const adverse = isAdverseLine(line, adverseRule);
          const amountText = formatMoney(line.amount, { region, currency });
          const meta = [
            line.date ? formatDate(line.date, region) : null,
            line.reference ?? null,
          ].filter((part): part is string => part !== null);
          const linked = line.href !== undefined || line.onPress !== undefined;
          return (
            <li
              key={line.id}
              className={styles.line}
              data-kind={line.kind ?? 'line'}
              data-adverse={adverse || undefined}
              data-linked={linked || undefined}
            >
              <div className={styles.description}>
                {linked ? (
                  <Link
                    className={cx(styles.rowLink)}
                    {...defined({ href: line.href, onPress: line.onPress })}
                  >
                    {line.description}
                  </Link>
                ) : (
                  <span>{line.description}</span>
                )}
                {meta.length > 0 ? (
                  <span className={styles.meta}>
                    {meta.map((part, index) => (
                      <span key={part} className={index > 0 ? styles.metaPart : undefined}>
                        {part}
                      </span>
                    ))}
                  </span>
                ) : null}
              </div>
              <span className={styles.amount}>
                {adverse ? <Icon icon={ArrowDownRight} size={14} /> : null}
                {amountText}
                {adverse ? (
                  <span className="ds-visually-hidden">{t('statementView.adverseNote')}</span>
                ) : null}
              </span>
              {showRunningBalance ? (
                <span className={styles.running}>
                  {line.kind === 'subtotal' ? null : (
                    <>
                      <span className="ds-visually-hidden">
                        {t('statementView.runningBalance')}
                      </span>
                      {formatMoney(after, { region, currency })}
                    </>
                  )}
                </span>
              ) : null}
            </li>
          );
        })}
      </ul>
    );
  }

  return (
    <section className={styles.statement} {...(title ? { 'aria-labelledby': titleId } : {})}>
      {title ? (
        <Heading level={headingLevel} className={styles.title} id={titleId}>
          {title}
        </Heading>
      ) : null}
      <div className={styles.headline}>
        {state === 'loading' ? (
          <SkeletonBlock width="200px" height="32px" shape="block" />
        ) : state === 'error' ? null : (
          <>
            <span className={styles.balance}>
              {formatMoney(direction === 'settled' ? 0 : Math.abs(balance), { region, currency })}
            </span>
            <span className={styles.direction} data-direction={direction}>
              {directionLabel}
            </span>
          </>
        )}
      </div>
      {periodSelector ? <div className={styles.period}>{periodSelector}</div> : null}
      {showRunningBalance && state === 'ready' && lines.length > 0 ? (
        <div className={styles.columns} aria-hidden="true">
          <span />
          <span>{t('statementView.amount')}</span>
          <span>{t('statementView.runningBalance')}</span>
        </div>
      ) : null}
      {content}
    </section>
  );
}
