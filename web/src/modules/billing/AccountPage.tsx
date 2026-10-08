import { useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import type { BillingAccountGetResponse } from '../../api/types';
import {
  KeyValueList,
  dateColumn,
  identifierColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { rememberRecent } from '../staff/recent';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { useBillingAccount, useInvoices } from './api';
import { InvoiceTable } from './InvoiceTable';
import { RecordPaymentForm } from './RecordPaymentForm';

type Term = BillingAccountGetResponse['account']['terms'][number];

function AccountDetails({ data }: { data: BillingAccountGetResponse }) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const { account, balancesByState: balances } = data;
  const invoices = useInvoices({ billingAccountId: account.billingAccountId });

  // The opaque record id comes from the route, not from the account payload.
  const { accountId: recordId = '' } = useParams();
  useEffect(() => {
    if (recordId) rememberRecent('account', recordId);
  }, [recordId]);

  const termColumns = useMemo<DataColumn<Term>[]>(
    () => [
      identifierColumn<Term>('policy', t('account.terms.policy'), (r) => r.policyNumber),
      textColumn<Term>('product', t('account.terms.product'), (r) => r.productCode),
      textColumn<Term>('plan', t('account.terms.plan'), (r) => r.planCode),
      textColumn<Term>('mode', t('account.terms.mode'), (r) => t(`account.billMode.${r.billMode}`)),
      dateColumn<Term>('from', t('account.terms.from'), (r) => r.termPeriod.from),
      dateColumn<Term>('to', t('account.terms.to'), (r) => r.termPeriod.to),
    ],
    [t],
  );

  return (
    <div className={styles.page}>
      <PageHeader
        overline={t('overline')}
        title={t('account.title', { number: account.accountNumber })}
        subtitle={<span className="ds-caption">{t(`account.status.${account.status}`)}</span>}
      />
      <div className={styles.grid}>
        <Section title={t('account.balances')}>
          <KeyValueList
            aria-label={t('account.balances')}
            items={[
              {
                id: 'unbilled',
                label: t('account.balance.writtenUnbilled'),
                value: fmt.money(balances.writtenUnbilled),
                kind: 'money',
              },
              {
                id: 'billed',
                label: t('account.balance.billed'),
                value: fmt.money(balances.billed),
                kind: 'money',
              },
              {
                id: 'overdue',
                label: t('account.balance.overdue'),
                value: fmt.money(balances.overdue),
                kind: 'money',
              },
              {
                id: 'collected',
                label: t('account.balance.collected'),
                value: fmt.money(balances.collected),
                kind: 'money',
              },
              {
                id: 'unapplied',
                label: t('account.balance.unapplied'),
                value: fmt.money(balances.unapplied),
                kind: 'money',
              },
              {
                id: 'credit',
                label: t('account.balance.credit'),
                value: fmt.money(balances.credit),
                kind: 'money',
              },
            ]}
          />
        </Section>
        <Section title={t('account.termsTitle')}>
          {account.terms.length > 0 ? (
            <SimpleTable<Term>
              aria-label={t('account.termsTitle')}
              columns={termColumns}
              data={account.terms}
              getRowId={(r) => r.policyTermId}
            />
          ) : (
            <p className={styles.muted}>{t('account.noTerms')}</p>
          )}
        </Section>
      </div>
      <Section title={t('account.invoices')}>
        <QueryView query={invoices}>
          {(page) => <InvoiceTable items={page.items} label={t('account.invoices')} />}
        </QueryView>
      </Section>
      <Section title={t('payment.title')}>
        <RecordPaymentForm
          billingAccountId={account.billingAccountId}
          currency={account.currency}
        />
      </Section>
    </div>
  );
}

/** Billing account: balances by state, the policy terms billed, its invoices and the record-payment form. */
export function AccountPage() {
  const { accountId = '' } = useParams();
  const { t } = useTranslation('billing');
  const query = useBillingAccount(accountId);
  return (
    <QueryView query={query} notFoundMessage={t('account.notFound')}>
      {(data) => <AccountDetails data={data} />}
    </QueryView>
  );
}
