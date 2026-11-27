import { today } from '@internationalized/date';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import {
  Banner,
  KeyValueList,
  TextField,
  moneyColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { compareMoney } from '../../../format';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useTrialBalance } from './api';
import type { TrialBalance, TrialBalanceRow } from './types';

const periodPattern = /^\d{4}-(0[1-9]|1[0-2])$/;

function Balances({ data }: { data: TrialBalance }) {
  const { t, i18n } = useTranslation('finance');
  const fmt = useFormat();
  const en = i18n.language.startsWith('en');
  const currency = data.currency;
  const columns: DataColumn<TrialBalanceRow>[] = [
    textColumn<TrialBalanceRow>('book', t('ops.trialBalance.book'), (r) => r.book, { size: 110 }),
    textColumn<TrialBalanceRow>(
      'account',
      t('ops.trialBalance.account'),
      (r) => `${r.account} · ${en ? r.nameEn : r.nameEl}`,
      { size: 340 },
    ),
    moneyColumn<TrialBalanceRow>('debit', t('ops.trialBalance.debit'), (r) => r.debit, {
      currency,
    }),
    moneyColumn<TrialBalanceRow>('credit', t('ops.trialBalance.credit'), (r) => r.credit, {
      currency,
    }),
    moneyColumn<TrialBalanceRow>('balance', t('ops.trialBalance.balance'), (r) => r.balance, {
      currency,
    }),
  ];
  const balanced = compareMoney(data.totalDebit, data.totalCredit) === 0;
  return (
    <>
      <SimpleTable<TrialBalanceRow>
        aria-label={t('ops.trialBalance.tableLabel', { period: data.period })}
        columns={columns}
        data={data.balances}
        getRowId={(r) => `${r.book}:${r.account}`}
        emptyState={t('ops.trialBalance.empty', { period: data.period })}
      />
      {data.balances.length > 0 ? (
        <div className={styles.stack}>
          <KeyValueList
            aria-label={t('ops.trialBalance.totals')}
            items={[
              {
                id: 'debit',
                label: t('ops.trialBalance.totalDebit'),
                value: fmt.money({ amount: data.totalDebit, currency }),
                kind: 'money',
              },
              {
                id: 'credit',
                label: t('ops.trialBalance.totalCredit'),
                value: fmt.money({ amount: data.totalCredit, currency }),
                kind: 'money',
              },
            ]}
          />
          {balanced ? (
            <Banner variant="success" live="none" title={t('ops.trialBalance.balanced')} />
          ) : (
            <Banner variant="danger" live="alert" title={t('ops.trialBalance.unbalanced')}>
              {t('ops.trialBalance.unbalancedBody')}
            </Banner>
          )}
        </div>
      ) : null}
    </>
  );
}

/** Trial balance of one calendar month (fin.Balance.trialBalance): per-account debit, credit and balance in the functional currency. */
export function TrialBalancePage() {
  const { t } = useTranslation('finance');
  const [period, setPeriod] = useState(() => today('Europe/Athens').toString().slice(0, 7));
  const [book, setBook] = useState('');
  const validPeriod = periodPattern.test(period.trim());
  const query = useTrialBalance(validPeriod ? period.trim() : '', book.trim());

  return (
    <div className={styles.page}>
      <PageHeader
        title={t('ops.trialBalance.title')}
        overline={t('overline')}
        actions={
          <LinkButton variant="secondary" to="/finance">
            {t('ops.back')}
          </LinkButton>
        }
      />
      <Section title={t('ops.trialBalance.filters')}>
        <div className={styles.row}>
          <TextField
            label={t('ops.trialBalance.period')}
            helperText={t('ops.trialBalance.periodHelp')}
            mono
            value={period}
            onChange={setPeriod}
            errorMessage={validPeriod ? undefined : t('ops.trialBalance.periodInvalid')}
          />
          <TextField
            label={t('ops.trialBalance.book')}
            helperText={t('ops.trialBalance.bookHelp')}
            mono
            maxLength={32}
            value={book}
            onChange={setBook}
          />
        </div>
      </Section>
      <Section title={t('ops.trialBalance.title')}>
        {validPeriod ? (
          <QueryView query={query}>{(data) => <Balances data={data} />}</QueryView>
        ) : (
          <p className={styles.muted}>{t('ops.trialBalance.periodInvalid')}</p>
        )}
      </Section>
    </div>
  );
}
