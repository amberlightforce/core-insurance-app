import { parseDate, today } from '@internationalized/date';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../api/idempotency';
import type { Money, PaymentTakeRequest, PaymentTakeResponse } from '../../api/types';
import {
  Banner,
  Button,
  Checkbox,
  CurrencyField,
  DatePicker,
  ErrorSummary,
  Select,
  TextField,
  announce,
  type ErrorSummaryItem,
} from '../../design-system';
import { compareMoney } from '../../format';
import { ProblemBanner } from '../staff/ProblemBanner';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { useTakePayment } from './api';

export interface RecordPaymentFormProps {
  billingAccountId: string;
  currency: string;
  /** When recording against one invoice: it is preselected and its open amount is the default and the limit. */
  invoice?: { invoiceId: string; invoiceNumber: string; open: Money };
}

const methods = ['BANK_TRANSFER', 'CASHIER'] as const;

/**
 * Record a payment (bil.Payment.take). One Idempotency-Key per submission, reused if the same payment is submitted
 * again after an error or timeout, so a retry can never record the payment twice.
 */
export function RecordPaymentForm({ billingAccountId, currency, invoice }: RecordPaymentFormProps) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const { keyFor, release } = useIdempotencyKey();
  const [amount, setAmount] = useState<string | null>(invoice?.open.amount ?? null);
  const [method, setMethod] = useState<string | null>('BANK_TRANSFER');
  const [valueDate, setValueDate] = useState<string>(today('Europe/Athens').toString());
  const [reference, setReference] = useState('');
  const [autoAllocate, setAutoAllocate] = useState(true);
  const [submitted, setSubmitted] = useState(0);
  const [recorded, setRecorded] = useState<PaymentTakeResponse | null>(null);

  const mutation = useTakePayment((response) => {
    release();
    setRecorded(response);
    setAmount(null);
    setReference('');
    announce(t('payment.recorded', { number: response.receipt.receiptNumber }));
  });

  const errors: Record<string, string> = {};
  if (amount === null || amount === '' || compareMoney(amount, '0.00') <= 0)
    errors['payment-amount'] = t('payment.errors.amount');
  else if (invoice && compareMoney(amount, invoice.open.amount) > 0)
    errors['payment-amount'] = t('payment.errors.overOpen', { open: fmt.money(invoice.open) });
  if (!method) errors['payment-method'] = t('payment.errors.method');
  if (method === 'BANK_TRANSFER' && reference.trim() === '')
    errors['payment-reference'] = t('payment.errors.reference');
  const summary: ErrorSummaryItem[] = Object.entries(errors).map(([fieldId, message]) => ({
    fieldId,
    message,
  }));
  const shown = submitted > 0 ? errors : {};

  const submit = () => {
    setSubmitted((n) => n + 1);
    setRecorded(null);
    if (Object.keys(errors).length > 0 || amount === null || method === null) return;
    const request: PaymentTakeRequest = {
      billingAccountId,
      amount: { amount, currency },
      method: method as (typeof methods)[number],
      valueDate,
      ...(reference.trim() ? { bankReference: reference.trim() } : {}),
      ...(invoice ? { invoiceId: invoice.invoiceId } : {}),
      autoAllocate,
    };
    mutation.mutate({ request, key: keyFor(request) });
  };

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('payment.title')}
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <ErrorSummary
        errors={summary.length > 0 && submitted > 0 ? summary : []}
        focusKey={submitted}
      />
      {invoice ? (
        <p className={styles.muted}>
          {t('payment.forInvoice', {
            number: invoice.invoiceNumber,
            open: fmt.money(invoice.open),
          })}
        </p>
      ) : null}
      <div className={styles.grid}>
        <CurrencyField
          label={t('payment.amount')}
          isRequired
          name="payment-amount"
          value={amount}
          onChange={setAmount}
          {...(shown['payment-amount'] ? { errorMessage: shown['payment-amount'] } : {})}
        />
        <Select
          label={t('payment.method')}
          id="payment-method"
          isRequired
          options={methods.map((m) => ({ id: m, label: t(`payment.methods.${m}`) }))}
          value={method}
          onChange={setMethod}
          errorMessage={shown['payment-method']}
        />
        <DatePicker
          label={t('payment.valueDate')}
          isRequired
          value={parseDate(valueDate)}
          onChange={(value) => {
            if (value) setValueDate(value.toString());
          }}
        />
        <TextField
          id="payment-reference"
          label={t('payment.reference')}
          isRequired={method === 'BANK_TRANSFER'}
          maxLength={140}
          value={reference}
          onChange={setReference}
          errorMessage={shown['payment-reference']}
        />
      </div>
      <Checkbox isSelected={autoAllocate} onChange={setAutoAllocate}>
        {t('payment.autoAllocate')}
      </Checkbox>
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('payment.failed')} />
      ) : null}
      {recorded ? (
        <Banner
          variant="success"
          live="status"
          title={t('payment.recorded', { number: recorded.receipt.receiptNumber })}
        >
          {t('payment.outcome', {
            amount: fmt.money(recorded.receipt.amount),
            outcome: t(`payment.allocation.${recorded.allocationOutcome}`),
          })}
        </Banner>
      ) : null}
      <div className={styles.actions}>
        <Button type="submit" variant="primary" isLoading={mutation.isPending}>
          {t('payment.submit')}
        </Button>
      </div>
    </form>
  );
}
