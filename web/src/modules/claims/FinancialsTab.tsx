import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { ClaimPaymentView, ClaimView, FinancialLineBalance } from '../../api/types';
import {
  EmptyState,
  KeyValueList,
  dateColumn,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { readRecent } from '../staff/recent';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { useFinancials, usePayeeAccounts, usePayments, useRefreshMoney } from './api';
import { PaymentStatusPill } from './MoneyPills';
import { PayeeAccountForm } from './PayeeAccountForm';
import { SetCard } from './SetViews';
import { TransactionBuilder } from './TransactionBuilder';

function Balances({ claim }: { claim: ClaimView }) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const query = useFinancials(claim.summary.claimId);
  const numbers = useMemo(
    () => new Map(claim.exposures.map((e) => [e.exposureId, e.exposureNumber] as const)),
    [claim.exposures],
  );
  const lines = useMemo<FinancialLineBalance[]>(
    () => query.data?.balancesByLine ?? [],
    [query.data],
  );
  const columns = useMemo<DataColumn<FinancialLineBalance>[]>(
    () => [
      textColumn<FinancialLineBalance>(
        'exposure',
        t('financials.columns.exposure'),
        (l) => numbers.get(l.exposureId) ?? null,
      ),
      textColumn<FinancialLineBalance>(
        'line',
        t('financials.columns.line'),
        (l) =>
          `${t(`codes.costType.${l.costType}`, { defaultValue: l.costType })} · ${t(`codes.costCategory.${l.costCategory}`, { defaultValue: l.costCategory })}`,
      ),
      moneyColumn<FinancialLineBalance>(
        'reserved',
        t('financials.columns.reserved'),
        (l) => l.reserved.amount,
        { currency: 'EUR' },
      ),
      moneyColumn<FinancialLineBalance>(
        'paid',
        t('financials.columns.paid'),
        (l) => l.paid.amount,
        { currency: 'EUR' },
      ),
      moneyColumn<FinancialLineBalance>(
        'open',
        t('financials.columns.openReserve'),
        (l) => l.openReserve.amount,
        { currency: 'EUR' },
      ),
      moneyColumn<FinancialLineBalance>(
        'incurred',
        t('financials.columns.incurred'),
        (l) => l.incurred.amount,
        { currency: 'EUR' },
      ),
      textColumn<FinancialLineBalance>('final', t('financials.columns.final'), (l) =>
        l.final ? t('financials.finalLine') : null,
      ),
    ],
    [t, numbers],
  );
  return (
    <QueryView query={query}>
      {(data) => (
        <div className={styles.stack}>
          <SimpleTable<FinancialLineBalance>
            aria-label={t('financials.lines')}
            columns={columns}
            data={lines}
            getRowId={(l) => l.reserveLineId}
            emptyState={
              <EmptyState
                kind="first-use"
                headingLevel={3}
                headline={t('financials.emptyTitle')}
                description={t('financials.emptyBody')}
              />
            }
          />
          <KeyValueList
            aria-label={t('financials.totals')}
            moneyOnly
            items={[
              {
                id: 'reserved',
                label: t('financials.columns.reserved'),
                value: fmt.money(data.totals.reserved),
                kind: 'money',
              },
              {
                id: 'paid',
                label: t('financials.columns.paid'),
                value: fmt.money(data.totals.paid),
                kind: 'money',
              },
              {
                id: 'open',
                label: t('financials.columns.openReserve'),
                value: fmt.money(data.totals.openReserve),
                kind: 'money',
              },
              {
                id: 'incurred',
                label: t('financials.columns.incurred'),
                value: fmt.money(data.totals.incurred),
                kind: 'money',
              },
            ]}
          />
        </div>
      )}
    </QueryView>
  );
}

function Payments({ claim }: { claim: ClaimView }) {
  const { t } = useTranslation('claims');
  const query = usePayments(claim.summary.claimId);
  const rows = useMemo<ClaimPaymentView[]>(() => query.data?.items ?? [], [query.data]);
  const columns = useMemo<DataColumn<ClaimPaymentView>[]>(
    () => [
      moneyColumn<ClaimPaymentView>(
        'amount',
        t('payments.columns.amount'),
        (p) => p.amount.amount,
        { currency: 'EUR' },
      ),
      textColumn<ClaimPaymentView>('type', t('payments.columns.type'), (p) =>
        p.paymentType
          ? t(`builder.paymentTypes.${p.paymentType}`, { defaultValue: p.paymentType })
          : null,
      ),
      statusColumn<ClaimPaymentView>(
        'status',
        t('payments.columns.status'),
        (p) => p.status,
        (p) => <PaymentStatusPill status={p.status} />,
      ),
      textColumn<ClaimPaymentView>(
        'account',
        t('payments.columns.account'),
        (p) => p.maskedAccount ?? null,
      ),
      textColumn<ClaimPaymentView>('hold', t('payments.columns.hold'), (p) =>
        p.holdReason ? t(`payments.hold.${p.holdReason}`, { defaultValue: p.holdReason }) : null,
      ),
      dateColumn<ClaimPaymentView>(
        'created',
        t('payments.columns.created'),
        (p) => p.createdAt ?? null,
      ),
    ],
    [t],
  );
  return (
    <QueryView query={query}>
      {() => (
        <SimpleTable<ClaimPaymentView>
          aria-label={t('payments.title')}
          columns={columns}
          data={rows}
          getRowId={(p) => p.claimPaymentId}
          emptyState={
            <EmptyState
              kind="first-use"
              headingLevel={3}
              headline={t('payments.emptyTitle')}
              description={t('payments.emptyBody')}
            />
          }
        />
      )}
    </QueryView>
  );
}

/** Financials tab: balances per line and totals, payments, the claim's transaction sets, and (while open) the builder. */
export function FinancialsTab({ claim }: { claim: ClaimView }) {
  const { t } = useTranslation('claims');
  const claimId = claim.summary.claimId;
  const open = claim.summary.status === 'OPEN';
  const payments = usePayments(claimId);
  const accounts = usePayeeAccounts(claimId);
  const refresh = useRefreshMoney(claimId);
  const insured =
    claim.claimants.find((c) => c.claimantType === 'INSURED')?.partyId ??
    claim.summary.insuredPartyId;

  // The API has no list of a claim's sets: show the ones this browser built plus those carrying a payment.
  // Sets built in this session show at once; earlier ones come from this browser's memory (newest first).
  const [justBuilt, setJustBuilt] = useState<string[]>([]);
  const remembered = [...justBuilt, ...readRecent(`claimset.${claimId}`)];
  const setIds = [
    ...new Set([
      ...remembered,
      ...((payments.data?.items ?? []) as ClaimPaymentView[]).map((p) => p.setId),
    ]),
  ];
  const accountItems = useMemo(() => accounts.data?.items ?? [], [accounts.data]);

  return (
    <div className={styles.stack}>
      <Section title={t('financials.title')}>
        <Balances claim={claim} />
      </Section>
      <Section title={t('payments.title')}>
        <Payments claim={claim} />
      </Section>
      <Section title={t('set.title')}>
        {setIds.length === 0 ? (
          <p className={styles.muted}>{t('set.none')}</p>
        ) : (
          <div className={styles.stack}>
            {setIds.map((id) => (
              <SetCard key={id} claimId={claimId} setId={id} />
            ))}
          </div>
        )}
      </Section>
      {open ? (
        <>
          <Section title={t('builder.title')}>
            <TransactionBuilder
              claimId={claimId}
              exposures={claim.exposures}
              accounts={accountItems}
              onSubmitted={(setId) => {
                setJustBuilt((ids) => [setId, ...ids]);
              }}
            />
          </Section>
          <Section title={t('payee.title')}>
            <PayeeAccountForm
              claimId={claimId}
              partyId={insured}
              onSaved={() => {
                void refresh();
              }}
            />
          </Section>
        </>
      ) : null}
    </div>
  );
}
