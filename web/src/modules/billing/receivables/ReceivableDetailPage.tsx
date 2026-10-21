import { parseDate, today } from '@internationalized/date';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import type { PaymentTakeRequest, PaymentTakeResponse } from '../../../api/types';
import {
  Banner,
  Button,
  CurrencyField,
  DatePicker,
  KeyValueList,
  TextField,
  announce,
} from '../../../design-system';
import { currencyFractionDigits, fromMinor, toMinor } from '../../../format';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useReceivable, useRecordReceipt, type ReceivableView } from './api';
import { ClaimLink, CounterpartyName, ReceivableStatusPill } from './ReceivableParts';

interface RecordedReceipt {
  receiptNumber: string;
  amount: { amount: string; currency: string };
  openAmountAfter: { amount: string; currency: string } | null;
  allocationId: string | null;
}

/**
 * «Καταχώριση είσπραξης»: bil.Payment.take with the receivable's payment reference. One Idempotency-Key per
 * submission, reused on retry (the payload is unchanged), released once the receipt is recorded. Amounts are
 * compared in BigInt minor units.
 */
function ReceiptForm({
  receivable,
  onRecorded,
}: {
  receivable: ReceivableView;
  onRecorded: (r: RecordedReceipt) => void;
}) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const { keyFor, release } = useIdempotencyKey();
  const currency = receivable.openAmount.currency;
  const digits = currencyFractionDigits(currency);
  const [amount, setAmount] = useState<string | null>(receivable.openAmount.amount);
  const [valueDate, setValueDate] = useState(today('Europe/Athens').toString());
  const [reference, setReference] = useState(receivable.paymentReference);
  const [tried, setTried] = useState(false);

  const mutation = useRecordReceipt((response: PaymentTakeResponse) => {
    release();
    onRecorded({
      receiptNumber: response.receipt.receiptNumber,
      amount: response.receipt.amount,
      openAmountAfter: response.receivableAllocation?.openAmountAfter ?? null,
      allocationId: response.receivableAllocation?.allocationId ?? null,
    });
    announce(t('receivables.receipt.recorded', { number: response.receipt.receiptNumber }));
  });

  const openMinor = toMinor(receivable.openAmount.amount, digits);
  let amountError: string | undefined;
  if (amount === null || amount === '' || toMinor(amount, digits) <= 0n)
    amountError = t('receivables.receipt.errors.amount');
  else if (toMinor(amount, digits) > openMinor)
    amountError = t('receivables.receipt.errors.overOpen', {
      open: fmt.money(receivable.openAmount),
    });
  const referenceError =
    reference.trim() === '' ? t('receivables.receipt.errors.reference') : undefined;

  const submit = () => {
    setTried(true);
    if (amountError || referenceError || amount === null) return;
    const request: PaymentTakeRequest = {
      billingAccountId: receivable.billingAccountId,
      amount: { amount: fromMinor(toMinor(amount, digits), digits), currency },
      method: 'BANK_TRANSFER',
      valueDate,
      paymentReference: reference.trim(),
      autoAllocate: true,
    };
    mutation.mutate({ request, key: keyFor(request) });
  };

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('receivables.receipt.title')}
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <p className={styles.muted}>{t('receivables.receipt.intro')}</p>
      <div className={styles.grid}>
        <CurrencyField
          label={t('receivables.receipt.amount')}
          isRequired
          name="receipt-amount"
          value={amount}
          onChange={setAmount}
          {...(tried && amountError ? { errorMessage: amountError } : {})}
        />
        <DatePicker
          label={t('receivables.receipt.valueDate')}
          isRequired
          value={parseDate(valueDate)}
          onChange={(value) => {
            if (value) setValueDate(value.toString());
          }}
        />
        <TextField
          id="receipt-reference"
          label={t('receivables.receipt.reference')}
          isRequired
          maxLength={64}
          value={reference}
          onChange={setReference}
          errorMessage={tried ? referenceError : undefined}
        />
      </div>
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('receivables.receipt.failed')} />
      ) : null}
      <div className={styles.actions}>
        <Button type="submit" variant="primary" isLoading={mutation.isPending}>
          {t('receivables.receipt.submit')}
        </Button>
      </div>
    </form>
  );
}

function ReceivableDetails({ receivable }: { receivable: ReceivableView }) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const [receipts, setReceipts] = useState<RecordedReceipt[]>([]);
  const digits = currencyFractionDigits(receivable.amount.currency);
  const paidMinor =
    toMinor(receivable.amount.amount, digits) - toMinor(receivable.openAmount.amount, digits);
  const paid = { amount: fromMinor(paidMinor, digits), currency: receivable.amount.currency };
  const payable = receivable.status === 'OPEN' || receivable.status === 'PARTIALLY_PAID';

  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('overline')}
        title={t('receivables.detail.title')}
        recordId={receivable.paymentReference}
        subtitle={<ReceivableStatusPill status={receivable.status} />}
        actions={
          <>
            <LinkButton variant="secondary" to={`/billing/accounts/${receivable.billingAccountId}`}>
              {t('refunds.detail.openAccount')}
            </LinkButton>
            <LinkButton variant="secondary" to="/billing/receivables">
              {t('receivables.backToList')}
            </LinkButton>
          </>
        }
      />
      <div className={styles.grid}>
        <Section title={t('receivables.detail.facts')}>
          <KeyValueList
            aria-label={t('receivables.detail.facts')}
            items={[
              {
                id: 'source',
                label: t('receivables.columns.source'),
                value: t(`receivables.source.${receivable.sourceType}`),
              },
              {
                id: 'purpose',
                label: t('receivables.columns.purpose'),
                value: t(`receivables.purpose.${receivable.purpose}`),
              },
              {
                id: 'counterparty',
                label: t('receivables.columns.counterparty'),
                value: <CounterpartyName partyId={receivable.counterpartyPartyId} />,
              },
              {
                id: 'claim',
                label: t('receivables.columns.claim'),
                value: <ClaimLink claimId={receivable.claimId} />,
              },
              {
                id: 'statement',
                label: t('receivables.detail.statementRef'),
                value: receivable.statementRef ?? null,
                kind: 'mono',
              },
              {
                id: 'reference',
                label: t('receivables.columns.reference'),
                value: receivable.paymentReference,
                kind: 'mono',
              },
              {
                id: 'due',
                label: t('receivables.columns.due'),
                value: fmt.date(receivable.dueDate),
              },
              {
                id: 'registered',
                label: t('receivables.detail.registeredAt'),
                value: fmt.dateTime(receivable.registeredAt),
              },
            ]}
          />
        </Section>
        <Section title={t('receivables.detail.amounts')}>
          <KeyValueList
            aria-label={t('receivables.detail.amounts')}
            items={[
              {
                id: 'amount',
                label: t('receivables.columns.amount'),
                value: fmt.money(receivable.amount),
                kind: 'money',
              },
              {
                id: 'paid',
                label: t('receivables.detail.paid'),
                value: fmt.money(paid),
                kind: 'money',
              },
              {
                id: 'open',
                label: t('receivables.columns.open'),
                value: fmt.money(receivable.openAmount),
                kind: 'money',
              },
            ]}
          />
        </Section>
      </div>
      <Section title={t('receivables.detail.allocations')}>
        <p className={styles.muted}>{t('receivables.detail.allocationsNote')}</p>
        {receipts.length === 0 ? (
          <p>{t('receivables.detail.noAllocations')}</p>
        ) : (
          <ul aria-label={t('receivables.detail.allocations')} className={styles.ruledList}>
            {receipts.map((r) => (
              <li key={r.receiptNumber}>
                {t('receivables.detail.allocationLine', {
                  number: r.receiptNumber,
                  amount: fmt.money(r.amount),
                })}
              </li>
            ))}
          </ul>
        )}
      </Section>
      <Section title={t('receivables.receipt.title')} family="plum">
        {payable ? (
          <ReceiptForm
            receivable={receivable}
            onRecorded={(r) => {
              setReceipts((prev) => [r, ...prev]);
            }}
          />
        ) : (
          <Banner variant="info" live="none" title={t('receivables.receipt.title')}>
            {t('receivables.receipt.notPayable')}
          </Banner>
        )}
        {receipts[0] ? (
          <Banner
            variant="success"
            live="status"
            title={t('receivables.receipt.recorded', { number: receipts[0].receiptNumber })}
          >
            {receipts[0].openAmountAfter
              ? t('receivables.receipt.openAfter', { open: fmt.money(receipts[0].openAmountAfter) })
              : null}
          </Banner>
        ) : null}
      </Section>
    </div>
  );
}

/** One receivable: facts, amounts, allocation history and the receipt form (Staff.Billing). */
export function ReceivableDetailPage() {
  const { receivableId = '' } = useParams();
  const { t } = useTranslation('billing');
  const query = useReceivable(receivableId);
  return (
    <QueryView query={query} notFoundMessage={t('receivables.notFound')}>
      {(data) => <ReceivableDetails receivable={data.receivable} />}
    </QueryView>
  );
}
