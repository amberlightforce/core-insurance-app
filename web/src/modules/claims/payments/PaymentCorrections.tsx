import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { isApiError } from '../../../api/client';
import { useIdempotencyKey } from '../../../api/idempotency';
import type { ClaimPayeeAccountView, ClaimPaymentView } from '../../../api/types';
import { Banner, Button, Select, announce } from '../../../design-system';
import { ProblemBanner } from '../../staff/ProblemBanner';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { PayeeAccountForm } from '../PayeeAccountForm';
import { PaymentStatusPill } from '../MoneyPills';
import { useCorrectPayment, type PaymentCorrection } from '../recoveries/api';

const correctionReasons = [
  'ISSUED_IN_ERROR',
  'WRONG_AMOUNT',
  'WRONG_PAYEE',
  'PAYEE_ACCOUNT_REJECTED',
  'CUSTOMER_REQUEST',
] as const;

/** Which corrections the payment's status allows; the server has the last word (CLM-ERR-NOT-STOPPABLE etc.). */
function allowedCorrections(status: string): PaymentCorrection[] {
  switch (status) {
    case 'PENDING':
    case 'APPROVED':
    case 'ON_HOLD':
      return ['void'];
    case 'SUBMITTED':
    case 'ISSUED':
      return ['stop'];
    case 'STOPPED':
    case 'VOIDED':
    case 'RETURNED':
    case 'REJECTED':
      return ['reissue'];
    default:
      return [];
  }
}

function CorrectionForm({
  claimId,
  claimPartyId,
  payment,
  action,
  accounts,
  onDone,
}: {
  claimId: string;
  claimPartyId: string;
  payment: ClaimPaymentView;
  action: PaymentCorrection;
  accounts: readonly ClaimPayeeAccountView[];
  onDone: () => void;
}) {
  const { t } = useTranslation('claims');
  const { keyFor, release } = useIdempotencyKey();
  const mutation = useCorrectPayment(claimId);
  const [reason, setReason] = useState<string | null>(null);
  const [accountId, setAccountId] = useState<string | null>(null);
  const [tried, setTried] = useState(false);
  const [capturing, setCapturing] = useState(false);
  const notStoppable =
    isApiError(mutation.error) && mutation.error.code === 'CLM-ERR-NOT-STOPPABLE';

  const submit = () => {
    setTried(true);
    if (!reason) return;
    const body = {
      paymentId: payment.claimPaymentId,
      reason,
      ...(action === 'reissue' && accountId ? { payeeAccountId: accountId } : {}),
    };
    mutation.mutate(
      { action, body, key: keyFor({ action, ...body }) },
      {
        onSuccess: () => {
          release();
          announce(t(`payFix.done.${action}`));
          onDone();
        },
      },
    );
  };

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t(`payFix.title.${action}`)}
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <p className={styles.muted}>{t(`payFix.intro.${action}`)}</p>
      <div className={styles.grid}>
        <Select
          label={t('payFix.reason')}
          isRequired
          options={correctionReasons.map((r) => ({ id: r, label: t(`payFix.reasons.${r}`) }))}
          value={reason}
          onChange={setReason}
          errorMessage={tried && !reason ? t('payFix.errors.reason') : undefined}
        />
        {action === 'reissue' ? (
          <Select
            label={t('payFix.newAccount')}
            helperText={t('payFix.newAccountHelp')}
            options={accounts.map((a) => ({ id: a.payeeAccountId, label: a.maskedIban }))}
            value={accountId}
            onChange={setAccountId}
          />
        ) : null}
      </div>
      {action === 'reissue' ? (
        capturing ? (
          <PayeeAccountForm
            claimId={claimId}
            partyId={claimPartyId}
            onSaved={(account) => {
              setAccountId(account.payeeAccountId);
              setCapturing(false);
            }}
          />
        ) : (
          <div className={styles.actions}>
            <Button
              variant="secondary"
              size="sm"
              onPress={() => {
                setCapturing(true);
              }}
            >
              {t('payFix.captureAccount')}
            </Button>
          </div>
        )
      ) : null}
      {notStoppable ? (
        <Banner variant="warning" live="alert" title={t('payFix.notStoppable.title')}>
          {t('payFix.notStoppable.body')}
        </Banner>
      ) : mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('payFix.failed')} />
      ) : null}
      <div className={styles.actions}>
        <Button
          type="submit"
          variant={action === 'reissue' ? 'primary' : 'danger'}
          isLoading={mutation.isPending}
        >
          {t(`payFix.submit.${action}`)}
        </Button>
        <Button variant="secondary" onPress={onDone}>
          {t('payFix.cancel')}
        </Button>
      </div>
    </form>
  );
}

/**
 * Payment corrections (REQ-CLM-125…127): void before release, stop after it, reissue to a (new) payee account.
 * Each needs a reason; the chips show the link between a reissue and the payment it replaces, and the fiscal MARK.
 */
export function PaymentCorrections({
  claimId,
  claimPartyId,
  payments,
  accounts,
  open,
}: {
  claimId: string;
  claimPartyId: string;
  payments: readonly ClaimPaymentView[];
  accounts: readonly ClaimPayeeAccountView[];
  open: boolean;
}) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const [active, setActive] = useState<{ id: string; action: PaymentCorrection } | null>(null);
  const numbered = new Map(payments.map((p, i) => [p.claimPaymentId, i + 1] as const));
  if (payments.length === 0) return null;
  return (
    <ul className={styles.stack} aria-label={t('payFix.list')}>
      {payments.map((p) => {
        const actions = open ? allowedCorrections(p.status) : [];
        const mine = active?.id === p.claimPaymentId ? active.action : null;
        return (
          <li key={p.claimPaymentId} className={styles.stack}>
            <div className={styles.tags}>
              <span className="ds-num">{fmt.money(p.amount)}</span>
              <PaymentStatusPill status={p.status} />
              {p.reissueOf ? (
                <span className="ds-caption">
                  {t('payFix.linked', { number: numbered.get(p.reissueOf) ?? '?' })}
                </span>
              ) : null}
              {p.fiscalMark ? (
                <span className="ds-caption">
                  {t('payFix.mark')} <span className="ds-mono">{p.fiscalMark}</span>
                </span>
              ) : null}
              {actions.map((action) => (
                <Button
                  key={action}
                  variant="secondary"
                  size="sm"
                  onPress={() => {
                    setActive(mine === action ? null : { id: p.claimPaymentId, action });
                  }}
                >
                  {t(`payFix.action.${action}`)}
                </Button>
              ))}
            </div>
            {mine ? (
              <CorrectionForm
                claimId={claimId}
                claimPartyId={claimPartyId}
                payment={p}
                action={mine}
                accounts={accounts}
                onDone={() => {
                  setActive(null);
                }}
              />
            ) : null}
          </li>
        );
      })}
    </ul>
  );
}
