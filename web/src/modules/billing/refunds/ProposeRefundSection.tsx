import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import { Banner, Button, Checkbox, KeyValueList, Select, TextField } from '../../../design-system';
import { compareMoney } from '../../../format/money-input';
import { ProblemBanner } from '../../staff/ProblemBanner';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  proposeRefund,
  useRefreshRefunds,
  type PayeeAccountCreateResponse,
  type RefundProposeRequest,
  type RefundView,
} from './api';
import { RefundPayeeForm } from './RefundPayeeForm';
import { BreakdownTable, NettingTable } from './RefundParts';
import { currentRoles, refundViewRoles } from './roles';

const reasons = ['CREDIT_BALANCE', 'CANCELLATION', 'OVERPAYMENT', 'OTHER'] as const;

export interface ProposeRefundSectionProps {
  billingAccountId: string;
  /** PTY party of the billing account's payer: the refund payee (D-SL3-14). */
  payerPartyId: string;
  /** Credit currently held on the account (balances.credit). */
  credit: { amount: string; currency: string };
}

/**
 * Entry point on the billing account: preview (dry run) and propose a refund of the account credit. The request
 * carries the account, the reason and an optional verified payee account only; the required authority and the
 * approval route are decided by the server (PITFALLS 4), the preview shows what it would decide.
 */
export function ProposeRefundSection({
  billingAccountId,
  payerPartyId,
  credit,
}: ProposeRefundSectionProps) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const navigate = useNavigate();
  const refresh = useRefreshRefunds();
  const { keyFor, release } = useIdempotencyKey();
  const [reason, setReason] = useState<string | null>(null);
  const [comment, setComment] = useState('');
  const [different, setDifferent] = useState(false);
  const [payeeId, setPayeeId] = useState<string | null>(null);
  const [tried, setTried] = useState(false);
  const [busy, setBusy] = useState<'preview' | 'propose' | null>(null);
  const [failure, setFailure] = useState<unknown>(null);
  const [preview, setPreview] = useState<RefundView | null>(null);

  const reasonOptions = useMemo(
    () => reasons.map((id) => ({ id, label: t(`refunds.propose.reasons.${id}`) })),
    [t],
  );
  const allowed = currentRoles().some((role) => refundViewRoles.includes(role));
  const hasCredit = compareMoney(credit.amount, '0', 4) > 0;
  if (!allowed) return null;
  if (!hasCredit) return <p className={styles.muted}>{t('refunds.propose.noCredit')}</p>;

  const buildRequest = (): RefundProposeRequest | null => {
    if (reason === null) return null;
    return {
      billingAccountId,
      reasonCode: reason,
      ...(different && payeeId ? { payeeAccountId: payeeId } : {}),
      ...(comment.trim() ? { comment: comment.trim() } : {}),
    };
  };

  const run = async (dryRun: boolean) => {
    setTried(true);
    const request = buildRequest();
    if (request === null || (different && payeeId === null)) return;
    setBusy(dryRun ? 'preview' : 'propose');
    setFailure(null);
    try {
      const { refund } = await proposeRefund(request, keyFor({ request, dryRun }), dryRun);
      if (dryRun) {
        setPreview(refund);
      } else {
        release();
        void refresh();
        void navigate(`/billing/refunds/${refund.refundId}`);
      }
    } catch (error) {
      setFailure(error);
    } finally {
      setBusy(null);
    }
  };

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('refunds.propose.title')}
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      <p className={styles.muted}>{t('refunds.propose.intro')}</p>
      <KeyValueList
        aria-label={t('refunds.propose.creditAvailable')}
        items={[
          {
            id: 'credit',
            label: t('refunds.propose.creditAvailable'),
            value: fmt.money(credit),
            kind: 'money',
          },
        ]}
      />
      <div className={styles.grid}>
        <Select
          label={t('refunds.propose.reason')}
          isRequired
          placeholder={t('refunds.propose.reasonPlaceholder')}
          options={reasonOptions}
          value={reason}
          onChange={(value) => {
            setReason(value);
            setPreview(null);
          }}
          errorMessage={tried && reason === null ? t('refunds.propose.reasonRequired') : undefined}
        />
        <TextField
          label={t('refunds.propose.comment')}
          multiline
          maxLength={1000}
          value={comment}
          onChange={(value) => {
            setComment(value);
            setPreview(null);
          }}
        />
      </div>
      <p className={styles.muted}>{t('refunds.propose.defaultPayee')}</p>
      <Checkbox
        isSelected={different}
        onChange={(selected) => {
          setDifferent(selected);
          setPreview(null);
          if (!selected) setPayeeId(null);
        }}
      >
        {t('refunds.propose.differentPayee')}
      </Checkbox>
      {different ? (
        <RefundPayeeForm
          partyId={payerPartyId}
          onSaved={(account: PayeeAccountCreateResponse) => {
            setPayeeId(account.payeeAccountId);
            setPreview(null);
          }}
        />
      ) : null}
      {tried && different && payeeId === null ? (
        <Banner variant="warning" live="alert" title={t('refunds.propose.needPayee')} />
      ) : null}
      {failure ? <ProblemBanner error={failure} title={t('refunds.propose.failed')} /> : null}
      {preview ? (
        <Banner variant="info" live="status" title={t('refunds.propose.previewTitle')}>
          <div className={styles.stack}>
            <p>{t('refunds.propose.previewNote')}</p>
            <KeyValueList
              aria-label={t('refunds.propose.previewTitle')}
              items={[
                {
                  id: 'amount',
                  label: t('refunds.detail.amount'),
                  value: fmt.money(preview.amount),
                  kind: 'money',
                },
                {
                  id: 'iban',
                  label: t('refunds.detail.maskedIban'),
                  value: preview.payee.maskedIban,
                  kind: 'mono',
                },
                {
                  id: 'approval',
                  label: t('refunds.detail.approvalState'),
                  value: t(`refunds.propose.approvalNote.${preview.approvalState}`),
                },
              ]}
            />
            <BreakdownTable lines={preview.breakdown} currency={preview.amount.currency} />
            <NettingTable lines={preview.netting} currency={preview.amount.currency} />
          </div>
        </Banner>
      ) : null}
      <div className={styles.actions}>
        <Button
          variant="secondary"
          isLoading={busy === 'preview'}
          isDisabled={busy !== null}
          onPress={() => {
            void run(true);
          }}
        >
          {t('refunds.propose.preview')}
        </Button>
        <Button
          variant="primary"
          isLoading={busy === 'propose'}
          isDisabled={busy !== null || preview === null}
          onPress={() => {
            void run(false);
          }}
        >
          {t('refunds.propose.propose')}
        </Button>
      </div>
    </form>
  );
}
