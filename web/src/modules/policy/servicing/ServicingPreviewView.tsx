import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import {
  Banner,
  KeyValueList,
  StatusPill,
  identifierColumn,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { Section } from '../../staff/PageHeader';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import type { ServicingPreview, ServicingProratedLine, ServicingTaxLine } from './api';
import { dueOf } from './logic';

/** Marks a tax or levy line whose rule is not Settled; it appears wherever the line does. */
function ProvisionalPill() {
  const { t } = useTranslation('policy');
  return (
    <StatusPill
      semantic="warning"
      text={t('servicing.preview.provisional')}
      announceChanges={false}
    />
  );
}

/** Refund due vs additional due, in one sentence at the top of every preview. */
export function DueBanner({ preview }: { preview: ServicingPreview }) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const due = dueOf(preview);
  if (due.kind === 'refund') {
    return (
      <Banner variant="info" live="status" title={t('servicing.preview.refundDue')}>
        <strong className={styles.money}>{fmt.money(due.amount)}</strong>
        {' · '}
        {t('servicing.preview.refundHelp')}
      </Banner>
    );
  }
  if (due.kind === 'additional') {
    return (
      <Banner variant="warning" live="status" title={t('servicing.preview.additionalDue')}>
        <strong className={styles.money}>{fmt.money(due.amount)}</strong>
        {' · '}
        {t('servicing.preview.additionalHelp')}
      </Banner>
    );
  }
  return (
    <Banner variant="info" live="status" title={t('servicing.preview.neutral')}>
      {t('servicing.preview.neutralHelp')}
    </Banner>
  );
}

/**
 * The servicing preview of a change, cancellation or renewal (REQ-POL-190, -206): refund due vs additional due,
 * the prorated premium lines (days, fraction, amount) and the tax lines with the treatment that decided them.
 * A provisional tax line is marked wherever it appears and the banner on top says so once (D-SL3-05).
 */
export function ServicingPreviewView({ preview }: { preview: ServicingPreview }) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const currency = preview.totalChange.currency;

  const proratedColumns = useMemo<DataColumn<ServicingProratedLine>[]>(
    () => [
      textColumn<ServicingProratedLine>(
        'cover',
        t('servicing.preview.columns.cover'),
        (l) => l.coverageCode,
        { size: 90 },
      ),
      identifierColumn<ServicingProratedLine>(
        'chargeType',
        t('servicing.preview.columns.charge'),
        (l) => l.chargeType,
        { size: 120 },
      ),
      textColumn<ServicingProratedLine>(
        'period',
        t('servicing.preview.columns.period'),
        (l) => `${fmt.date(l.period.from)} – ${fmt.date(l.period.to)}`,
        { size: 190 },
      ),
      textColumn<ServicingProratedLine>(
        'days',
        t('servicing.preview.columns.days'),
        (l) => `${String(l.days)} / ${String(l.termDays)}`,
        { size: 90 },
      ),
      textColumn<ServicingProratedLine>(
        'fraction',
        t('servicing.preview.columns.fraction'),
        (l) => l.fraction,
        { size: 100 },
      ),
      moneyColumn<ServicingProratedLine>(
        'amount',
        t('servicing.preview.columns.amount'),
        (l) => l.amount.amount,
        { currency },
      ),
    ],
    [t, fmt, currency],
  );

  const taxColumns = useMemo<DataColumn<ServicingTaxLine>[]>(
    () => [
      identifierColumn<ServicingTaxLine>(
        'chargeType',
        t('servicing.preview.columns.charge'),
        (l) => l.chargeType,
        { size: 90 },
      ),
      textColumn<ServicingTaxLine>(
        'treatment',
        t('servicing.preview.columns.treatment'),
        (l) => t(`servicing.treatment.${l.treatmentAction}`, { defaultValue: l.treatmentAction }),
        { size: 210 },
      ),
      textColumn<ServicingTaxLine>(
        'rule',
        t('servicing.preview.columns.rule'),
        (l) => `${l.ruleId} v${l.ruleVersion}`,
        { size: 160 },
      ),
      statusColumn<ServicingTaxLine>(
        'legal',
        t('servicing.preview.columns.legalStatus'),
        (l) => l.legalStatus,
        (l) =>
          l.provisional ? (
            <ProvisionalPill />
          ) : (
            <span>
              {t(`servicing.legalStatus.${l.legalStatus}`, { defaultValue: l.legalStatus })}
            </span>
          ),
        { size: 150 },
      ),
      moneyColumn<ServicingTaxLine>(
        'amount',
        t('servicing.preview.columns.amount'),
        (l) => l.amount.amount,
        { currency },
      ),
    ],
    [t, currency],
  );

  const prorated = preview.proratedLines;
  const tax = preview.taxLines;
  return (
    <div className={styles.stack}>
      {preview.provisional ? (
        <Banner variant="warning" live="status" title={t('servicing.preview.provisionalTitle')}>
          {t('servicing.preview.provisionalBody')}
        </Banner>
      ) : null}
      <DueBanner preview={preview} />
      <Section title={t('servicing.preview.summary')} headingLevel={3}>
        <KeyValueList
          aria-label={t('servicing.preview.summary')}
          items={[
            {
              id: 'before',
              label: t('servicing.preview.annualBefore'),
              value: fmt.money(preview.annualBefore),
              kind: 'money',
            },
            {
              id: 'after',
              label: t('servicing.preview.annualAfter'),
              value: fmt.money(preview.annualAfter),
              kind: 'money',
            },
            {
              id: 'premium',
              label: t('servicing.preview.premiumChange'),
              value: fmt.money(preview.premiumChange),
              kind: 'money',
            },
            {
              id: 'tax',
              label: t('servicing.preview.taxChange'),
              value: fmt.money(preview.taxChange),
              kind: 'money',
            },
            {
              id: 'total',
              label: t('servicing.preview.totalChange'),
              value: fmt.money(preview.totalChange),
              kind: 'money',
            },
            ...(preview.refundMethod
              ? [
                  {
                    id: 'method',
                    label: t('servicing.preview.refundMethod'),
                    value: t(`servicing.refundMethod.${preview.refundMethod}`, {
                      defaultValue: preview.refundMethod,
                    }),
                  },
                ]
              : []),
          ]}
        />
      </Section>
      <Section title={t('servicing.preview.prorated')} headingLevel={3}>
        {prorated.length > 0 ? (
          <SimpleTable<ServicingProratedLine>
            aria-label={t('servicing.preview.proratedTable')}
            columns={proratedColumns}
            data={prorated}
            getRowId={(l) => `${l.elementLocator}:${l.coverageCode}:${l.chargeType}`}
            footerTotal={{
              label: t('servicing.preview.premiumChange'),
              value: preview.premiumChange.amount,
              currency,
            }}
          />
        ) : (
          <p className={styles.muted}>{t('servicing.preview.noProrated')}</p>
        )}
      </Section>
      <Section title={t('servicing.preview.taxes')} headingLevel={3}>
        {tax.length > 0 ? (
          <SimpleTable<ServicingTaxLine>
            aria-label={t('servicing.preview.taxTable')}
            columns={taxColumns}
            data={tax}
            getRowId={(l) => `${l.elementLocator}:${l.chargeCategory}:${l.chargeType}`}
            footerTotal={{
              label: t('servicing.preview.taxChange'),
              value: preview.taxChange.amount,
              currency,
            }}
          />
        ) : (
          <p className={styles.muted}>{t('servicing.preview.noTaxes')}</p>
        )}
      </Section>
    </div>
  );
}
