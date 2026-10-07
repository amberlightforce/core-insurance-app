import { Check } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

import { formatDate, type DateInput } from '../../../format/dates';
import { formatMoney, formatNumber, formatPercent, type Numeric } from '../../../format/numbers';
import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx } from '../../utils/cx';
import styles from './DataTable.module.css';

const isEmpty = (value: unknown) => value === null || value === undefined || value === '';

/** «—» in text.tertiary with the accessible name «κενό». */
export function EmptyValue() {
  const { t } = useTranslation('ds');
  return (
    <span className={styles.empty}>
      <span aria-hidden="true">—</span>
      <span className="ds-visually-hidden">{t('dataTable.emptyValue')}</span>
    </span>
  );
}

/** Text: single line with ellipsis; the full text is in the title tooltip. */
export function TextCell({ value }: { value: string | null | undefined }) {
  if (isEmpty(value)) return <EmptyValue />;
  return (
    <span className={styles.truncate} title={value}>
      {value}
    </span>
  );
}

/** Identifier: mono with tabular, slashed-zero numerals. */
export function IdentifierCell({ value }: { value: string | null | undefined }) {
  if (isEmpty(value)) return <EmptyValue />;
  return (
    <span className={cx(styles.truncate, styles.mono)} title={value}>
      {value}
    </span>
  );
}

export interface MoneyCellProps {
  value: Numeric | null | undefined;
  currency?: string;
  /** Adverse money uses text.adverse and «−»; negative values are adverse unless told otherwise. */
  adverse?: boolean;
}

/** Money: right-aligned, tabular, el-GR, U+2212. Never animates. */
export function MoneyCell({ value, currency = 'EUR', adverse }: MoneyCellProps) {
  const region = useRegionFormat();
  if (value === null || value === undefined || value === '') return <EmptyValue />;
  const negative = typeof value === 'string' ? value.trim().startsWith('-') : value < 0;
  const isAdverse = adverse ?? negative;
  return (
    <span className={styles.numeric} data-adverse={isAdverse || undefined}>
      {formatMoney(value, { currency, region })}
    </span>
  );
}

/** Percentage with fixed decimals per column. */
export function PercentCell({
  value,
  fractionDigits = 2,
}: {
  value: Numeric | null | undefined;
  fractionDigits?: number;
}) {
  const region = useRegionFormat();
  if (value === null || value === undefined || value === '') return <EmptyValue />;
  return <span className={styles.numeric}>{formatPercent(value, { region, fractionDigits })}</span>;
}

/** Date dd/MM/yyyy. */
export function DateCell({ value }: { value: DateInput | null | undefined }) {
  const region = useRegionFormat();
  if (value === null || value === undefined || value === '') return <EmptyValue />;
  return <span className={styles.tabular}>{formatDate(value, region)}</span>;
}

/** Priority: 64×6 accent bar + the number (priority is not a status: one colour only). */
export function PriorityCell({
  value,
  max = 100,
}: {
  value: number | null | undefined;
  max?: number;
}) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  if (value === null || value === undefined) return <EmptyValue />;
  const ratio = Math.min(1, Math.max(0, value / max));
  const text = formatNumber(value, { region, maximumFractionDigits: 0 });
  return (
    <span className={styles.priority}>
      <span className={styles.priorityTrack} aria-hidden="true">
        <span className={styles.priorityFill} style={{ transform: `scaleX(${String(ratio)})` }} />
      </span>
      <span className={styles.tabular} aria-hidden="true">
        {text}
      </span>
      <span className="ds-visually-hidden">
        {t('dataTable.priority', {
          value: text,
          max: formatNumber(max, { region, maximumFractionDigits: 0 }),
        })}
      </span>
    </span>
  );
}

/** Boolean: a check («Ναι») or «—». */
export function BooleanCell({ value }: { value: boolean | null | undefined }) {
  const { t } = useTranslation('ds');
  if (!value) return <EmptyValue />;
  return (
    <span className={styles.boolean}>
      <Icon icon={Check} size={16} label={t('dataTable.yes')} />
    </span>
  );
}

export interface QueueCellProps {
  /** Primary line (13.5 px class, weight 500), e.g. the client name. */
  primary: ReactNode;
  /** Mono business id on the secondary line. */
  id?: string;
  /** One fact after the 3 px dot, e.g. the product. */
  fact?: ReactNode;
}

/** Two-line queue row content (v3): primary line + «mono id · fact». */
export function QueueCell({ primary, id, fact }: QueueCellProps) {
  return (
    <span className={styles.queue}>
      <span className={styles.queuePrimary}>{primary}</span>
      <span className={styles.queueSecondary}>
        {id ? <span className={styles.queueId}>{id}</span> : null}
        {id && fact ? <span className={styles.queueDot} aria-hidden="true" /> : null}
        {fact ? <span className={styles.truncate}>{fact}</span> : null}
      </span>
    </span>
  );
}
