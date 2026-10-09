import { useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import type { InvoiceGetResponse } from '../../api/types';
import { compareMoney } from '../../format/money-input';
import {
  Banner,
  KeyValueList,
  StatusPill,
  dateColumn,
  identifierColumn,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { rememberRecent } from '../staff/recent';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { useInvoice } from './api';
import { FiscalStatusPill } from './FiscalStatusPill';
import { InvoiceStatePill } from './InvoiceStatePill';
import { RecordPaymentForm } from './RecordPaymentForm';

type Item = InvoiceGetResponse['invoiceItems'][number];
type Allocation = InvoiceGetResponse['allocations'][number];

function InvoiceDetails({ data }: { data: InvoiceGetResponse }) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const { invoice, fiscalStatus } = data;
  useEffect(() => {
    rememberRecent('invoice', invoice.invoiceId);
  }, [invoice.invoiceId]);

  const itemColumns = useMemo<DataColumn<Item>[]>(
    () => [
      textColumn<Item>('coverage', t('lines.columns.coverage'), (i) => i.coverageCode),
      identifierColumn<Item>('chargeType', t('lines.columns.charge'), (i) => i.chargeType, {
        size: 160,
      }),
      textColumn<Item>('category', t('lines.columns.category'), (i) => i.chargeCategory),
      textColumn<Item>(
        'txKind',
        t('refunds.lines.transactionKind'),
        (i) => i.transactionKind ?? null,
      ),
      textColumn<Item>('rule', t('refunds.lines.rule'), (i) => i.treatmentRuleId ?? null),
      statusColumn<Item>(
        'legal',
        t('lines.columns.legalStatus'),
        (i) => i.legalStatus ?? null,
        (i) =>
          i.provisional === true ? (
            <StatusPill
              semantic="warning"
              subLabel={t('lines.provisional')}
              announceChanges={false}
            />
          ) : i.legalStatus ? (
            <span>{i.legalStatus}</span>
          ) : null,
        { size: 240 },
      ),
      dateColumn<Item>('from', t('lines.columns.from'), (i) => i.validPeriod.from),
      moneyColumn<Item>('amount', t('lines.columns.amount'), (i) => i.amount.amount, {
        currency: invoice.total.currency,
      }),
      moneyColumn<Item>('open', t('lines.columns.open'), (i) => i.open.amount, {
        currency: invoice.total.currency,
      }),
      textColumn<Item>('state', t('lines.columns.state'), (i) => i.state),
    ],
    [t, invoice.total.currency],
  );
  const allocationColumns = useMemo<DataColumn<Allocation>[]>(
    () => [
      dateColumn<Allocation>('at', t('allocations.columns.date'), (a) => a.recordedAt),
      moneyColumn<Allocation>('amount', t('allocations.columns.amount'), (a) => a.amount.amount, {
        currency: invoice.total.currency,
      }),
      textColumn<Allocation>('source', t('allocations.columns.source'), (a) =>
        t(`allocations.source.${a.source}`),
      ),
      textColumn<Allocation>('rule', t('allocations.columns.rule'), (a) => a.ruleId),
    ],
    [t, invoice.total.currency],
  );
  const payable = invoice.kind === 'INVOICE' && compareMoney(invoice.open.amount, '0', 4) > 0;

  return (
    <div className={styles.page}>
      <PageHeader
        overline={t('overline')}
        title={t('invoice.title', { number: invoice.invoiceNumber })}
        subtitle={
          <>
            <InvoiceStatePill state={invoice.state} />
            <span className="ds-caption">
              {invoice.kind === 'INVOICE'
                ? t('invoice.kind.INVOICE')
                : t('invoice.kind.CREDIT_NOTE')}
            </span>
          </>
        }
        actions={
          <>
            <LinkButton variant="secondary" to={`/billing/accounts/${invoice.billingAccountId}`}>
              {t('invoice.openAccount')}
            </LinkButton>
            <LinkButton variant="secondary" to={`/policies/${invoice.policyId}`}>
              {t('invoice.openPolicy')}
            </LinkButton>
          </>
        }
      />
      {invoice.kind === 'CREDIT_NOTE' ? (
        <Banner variant="info" title={t('refunds.creditNote.bannerTitle')}>
          <p>{t('refunds.creditNote.bannerBody')}</p>
          {invoice.originalInvoiceId ? (
            <LinkButton variant="secondary" to={`/billing/invoices/${invoice.originalInvoiceId}`}>
              {t('refunds.creditNote.openOriginal')}
            </LinkButton>
          ) : null}
        </Banner>
      ) : null}
      <div className={styles.grid}>
        <Section title={t('invoice.totals')}>
          <KeyValueList
            aria-label={t('invoice.totals')}
            items={[
              {
                id: 'issue',
                label: t('invoice.columns.issueDate'),
                value: fmt.date(invoice.issueDate),
              },
              { id: 'due', label: t('invoice.columns.dueDate'), value: fmt.date(invoice.dueDate) },
              {
                id: 'total',
                label: t('invoice.columns.total'),
                value: fmt.money(invoice.total),
                kind: 'money',
              },
              {
                id: 'paid',
                label: t('invoice.paid'),
                value: fmt.money(invoice.paid),
                kind: 'money',
              },
              {
                id: 'open',
                label: t('invoice.columns.open'),
                value: fmt.money(invoice.open),
                kind: 'money',
              },
              ...invoice.totalsByCategory.map((c, index) => ({
                id: `cat-${String(index)}`,
                label: t('invoice.category', { category: c.category }),
                value: fmt.money(c.amount),
                kind: 'money' as const,
              })),
            ]}
          />
        </Section>
        <Section title={t('fiscal.title')}>
          <Banner variant="info" title={t('fiscal.stubTitle')}>
            {t('fiscal.stubBody')}
          </Banner>
          <KeyValueList
            aria-label={t('fiscal.title')}
            items={[
              {
                id: 'status',
                label: t('fiscal.statusLabel'),
                value: <FiscalStatusPill status={fiscalStatus.status} />,
              },
              { id: 'series', label: t('fiscal.series'), value: fiscalStatus.series, kind: 'mono' },
              { id: 'number', label: t('fiscal.number'), value: fiscalStatus.number, kind: 'mono' },
              { id: 'mark', label: t('fiscal.mark'), value: fiscalStatus.mark, kind: 'mono' },
              { id: 'uid', label: t('fiscal.uid'), value: fiscalStatus.uid, kind: 'mono' },
            ]}
          />
        </Section>
      </div>
      <Section title={t('lines.title')}>
        <SimpleTable<Item>
          aria-label={t('lines.title')}
          columns={itemColumns}
          data={data.invoiceItems}
          getRowId={(i) => i.invoiceItemId}
          footerTotal={{
            label: t('invoice.columns.total'),
            value: invoice.total.amount,
            currency: invoice.total.currency,
          }}
        />
      </Section>
      <Section title={t('allocations.title')}>
        {data.allocations.length > 0 ? (
          <SimpleTable<Allocation>
            aria-label={t('allocations.title')}
            columns={allocationColumns}
            data={data.allocations}
            getRowId={(a) => a.allocationId}
          />
        ) : (
          <p className={styles.muted}>{t('allocations.none')}</p>
        )}
      </Section>
      <Section title={t('payment.title')}>
        {payable ? (
          <RecordPaymentForm
            billingAccountId={invoice.billingAccountId}
            currency={invoice.total.currency}
            invoice={{
              invoiceId: invoice.invoiceId,
              invoiceNumber: invoice.invoiceNumber,
              open: invoice.open,
            }}
          />
        ) : (
          <p className={styles.muted}>{t('payment.nothingOpen')}</p>
        )}
      </Section>
    </div>
  );
}

/** One invoice with its lines, totals, fiscal document (stub) and the record-payment form. */
export function InvoicePage() {
  const { invoiceId = '' } = useParams();
  const { t } = useTranslation('billing');
  const query = useInvoice(invoiceId);
  return (
    <QueryView query={query} notFoundMessage={t('invoice.notFound')}>
      {(data) => <InvoiceDetails data={data} />}
    </QueryView>
  );
}
