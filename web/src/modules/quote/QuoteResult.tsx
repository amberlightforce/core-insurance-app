import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import type { ChargeLine, JobQuoteResponse, UwIssue } from '../../api/types';
import {
  Banner,
  StatusPill,
  identifierColumn,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { fromMinor, toMinor } from '../../format';
import { Section } from '../staff/PageHeader';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { ratingWarnings, warningCodes } from './state';

export interface QuoteResultProps {
  quote: JobQuoteResponse;
  /** Cover code → localised name from the product catalogue. */
  coverNames: ReadonlyMap<string, string>;
  /** The product's tariff and question data are flagged illustrative (test data). */
  productIsIllustrative: boolean;
}

/** Rating warnings, shown prominently above the figures (never hidden behind a disclosure). */
export function QuoteWarnings({ codes }: { codes: readonly string[] }) {
  const { t } = useTranslation('quote');
  return (
    <>
      {codes.includes(warningCodes.illustrativeTariff) ? (
        <Banner variant="warning" live="status" title={t('warnings.illustrativeTariff.title')}>
          {t('warnings.illustrativeTariff.body')}
          <span className="ds-caption"> {warningCodes.illustrativeTariff}</span>
        </Banner>
      ) : null}
      {codes.includes(warningCodes.provisionalTax) ? (
        <Banner variant="warning" live="status" title={t('warnings.provisionalTax.title')}>
          {t('warnings.provisionalTax.body')}
          <span className="ds-caption"> {warningCodes.provisionalTax}</span>
        </Banner>
      ) : null}
    </>
  );
}

function decisionVariant(decision: JobQuoteResponse['decision']): 'success' | 'warning' | 'danger' {
  return decision === 'ACCEPT' ? 'success' : decision === 'REFER' ? 'warning' : 'danger';
}

/** Underwriting outcome: accept, refer or decline, with the reasons (issues) behind it. */
export function UnderwritingOutcome({ quote }: { quote: JobQuoteResponse }) {
  const { t, i18n } = useTranslation('quote');
  const columns = useMemo<DataColumn<UwIssue>[]>(() => {
    const reasonText = (key: string) =>
      i18n.exists(`quote:uw.reasons.${key}`) ? t(`uw.reasons.${key}`) : key;
    return [
      textColumn<UwIssue>('issue', t('uw.columns.issue'), (i) => i.issueKey, { size: 220 }),
      textColumn<UwIssue>(
        'reasons',
        t('uw.columns.reasons'),
        (i) => (i.explanationKeys ?? []).map(reasonText).join('; ') || null,
        { size: 320 },
      ),
      textColumn<UwIssue>('point', t('uw.columns.point'), (i) => t(`uw.points.${i.blockingPoint}`)),
      textColumn<UwIssue>('approval', t('uw.columns.approval'), (i) => i.approvalStatus),
    ];
  }, [t, i18n]);
  return (
    <Section title={t('uw.title')} headingLevel={3}>
      <Banner variant={decisionVariant(quote.decision)} title={t(`uw.decision.${quote.decision}`)}>
        {t(`uw.decisionBody.${quote.decision}`)}
      </Banner>
      {quote.issues.length > 0 ? (
        <SimpleTable<UwIssue>
          aria-label={t('uw.issues')}
          columns={columns}
          data={quote.issues}
          getRowId={(i) => i.issueId}
        />
      ) : (
        <p className={styles.muted}>{t('uw.noIssues')}</p>
      )}
    </Section>
  );
}

function CoverTable({
  coverCode,
  name,
  lines,
}: {
  coverCode: string;
  name: string;
  lines: ChargeLine[];
}) {
  const { t } = useTranslation('quote');
  const currency = lines[0]?.amount.currency ?? 'EUR';
  const columns = useMemo<DataColumn<ChargeLine>[]>(
    () => [
      identifierColumn<ChargeLine>(
        'chargeType',
        t('breakdown.columns.charge'),
        (l) => l.chargeType,
        { size: 180 },
      ),
      textColumn<ChargeLine>('category', t('breakdown.columns.category'), (l) =>
        l.chargeCategory === 'PREMIUM'
          ? t('breakdown.category.PREMIUM')
          : l.chargeCategory === 'LEVY'
            ? t('breakdown.category.LEVY')
            : t('breakdown.category.TAX'),
      ),
      statusColumn<ChargeLine>(
        'legal',
        t('breakdown.columns.legalStatus'),
        (l) => l.legalStatus ?? null,
        (l) =>
          l.provisional === true || (l.legalStatus && l.legalStatus.toUpperCase() !== 'SETTLED') ? (
            <StatusPill
              semantic="warning"
              subLabel={t('breakdown.provisional')}
              announceChanges={false}
            />
          ) : l.legalStatus ? (
            <span>{l.legalStatus}</span>
          ) : null,
      ),
      moneyColumn<ChargeLine>('amount', t('breakdown.columns.amount'), (l) => l.amount.amount, {
        currency,
      }),
    ],
    [t, currency],
  );
  const subtotal = fromMinor(lines.reduce((sum, l) => sum + toMinor(l.amount.amount), 0n));
  return (
    <div className={styles.stack}>
      <h4 className="ds-heading-3">{`${name} · ${coverCode}`}</h4>
      <SimpleTable<ChargeLine>
        aria-label={t('breakdown.coverTable', { cover: name })}
        columns={columns}
        data={lines}
        getRowId={(l) => l.chargeId ?? `${l.coverageCode}:${l.chargeType}`}
        footerTotal={{ label: t('breakdown.subtotal'), value: subtotal, currency }}
      />
    </div>
  );
}

/** Premium breakdown per cover (premium lines then that cover's tax and levy lines) and the quote totals. */
export function PremiumBreakdown({
  quote,
  coverNames,
}: Pick<QuoteResultProps, 'quote' | 'coverNames'>) {
  const { t } = useTranslation('quote');
  const fmt = useFormat();
  const groups = useMemo(() => {
    const map = new Map<string, ChargeLine[]>();
    for (const line of quote.charges) {
      const list = map.get(line.coverageCode) ?? [];
      list.push(line);
      map.set(line.coverageCode, list);
    }
    // Premium lines first inside a cover, then taxes and levies.
    for (const list of map.values())
      list.sort(
        (a, b) => Number(a.chargeCategory !== 'PREMIUM') - Number(b.chargeCategory !== 'PREMIUM'),
      );
    return [...map.entries()];
  }, [quote.charges]);
  return (
    <Section title={t('breakdown.title')} headingLevel={3}>
      {groups.map(([code, lines]) => (
        <CoverTable key={code} coverCode={code} name={coverNames.get(code) ?? code} lines={lines} />
      ))}
      <dl className={styles.stack} aria-label={t('breakdown.totals')}>
        <div className={styles.row}>
          <dt>{t('breakdown.premium')}</dt>
          <dd className={styles.money}>{fmt.money(quote.premium)}</dd>
        </div>
        <div className={styles.row}>
          <dt>{t('breakdown.taxes')}</dt>
          <dd className={styles.money}>{fmt.money(quote.taxes)}</dd>
        </div>
        <div className={styles.row}>
          <dt>
            <strong>{t('breakdown.total')}</strong>
          </dt>
          <dd className={styles.money}>
            <strong>{fmt.money(quote.total)}</strong>
          </dd>
        </div>
      </dl>
      {quote.validUntil ? (
        <p className={styles.muted}>
          {t('breakdown.validUntil', { date: fmt.dateTime(quote.validUntil) })}
        </p>
      ) : null}
    </Section>
  );
}

/** The whole quote outcome: warnings first, then underwriting and the premium breakdown. */
export function QuoteResult({ quote, coverNames, productIsIllustrative }: QuoteResultProps) {
  const codes = useMemo(
    () => ratingWarnings(quote, productIsIllustrative),
    [quote, productIsIllustrative],
  );
  return (
    <div className={styles.stack}>
      <QuoteWarnings codes={codes} />
      <UnderwritingOutcome quote={quote} />
      <PremiumBreakdown quote={quote} coverNames={coverNames} />
    </div>
  );
}
