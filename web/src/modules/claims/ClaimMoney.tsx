import { useTranslation } from 'react-i18next';

import type { ClaimPaymentView, ClaimView } from '../../api/types';
import { Button, SkeletonBlock } from '../../design-system';
import { cx } from '../../design-system/utils/cx';
import { useFormat } from '../staff/useFormat';
import { useFinancials, usePayments } from './api';
import styles from './ClaimFile.module.css';
import { PaymentStatusPill } from './MoneyPills';

const awaitingRelease = new Set(['PENDING', 'APPROVED', 'ON_HOLD']);

export interface ClaimMoneyProps {
  claim: ClaimView;
  /** «Νέα πληρωμή»: opens the transaction builder (Οικονομικά tab). */
  onNewPayment?: () => void;
  /** «Αποδέσμευση πληρωμής»: the approvals that release payments. */
  onRelease?: () => void;
}

/**
 * Money summary of the claim file (mockup right column): incurred as the big number, paid + open reserve under
 * it, the payments with their status, and the two money actions. Values come from clm financials and payments.
 */
export function ClaimMoney({ claim, onNewPayment, onRelease }: ClaimMoneyProps) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const claimId = claim.summary.claimId;
  const financials = useFinancials(claimId);
  const payments = usePayments(claimId);
  const items = (payments.data?.items ?? []) as ClaimPaymentView[];
  const open = claim.summary.status === 'OPEN';
  const pending = items.filter((p) => awaitingRelease.has(p.status)).length;
  const totals = financials.data?.totals;

  return (
    <div className={cx(styles.money)}>
      {financials.isError ? (
        <p className={cx(styles.muted)}>{t('file.money.unavailable')}</p>
      ) : totals ? (
        <div className={cx(styles.incurred)}>
          <span className={cx(styles.incurredLabel)}>{t('file.money.incurred')}</span>
          <span className={cx(styles.incurredValue)}>{fmt.money(totals.incurred)}</span>
          <span className={cx(styles.incurredSplit)}>
            {t('file.money.split', {
              paid: fmt.money(totals.paid),
              reserve: fmt.money(totals.openReserve),
            })}
          </span>
        </div>
      ) : (
        <div aria-busy="true">
          <SkeletonBlock width="40%" />
          <SkeletonBlock width="70%" />
        </div>
      )}
      {items.length > 0 ? (
        <ul className={cx(styles.payments)} aria-label={t('payments.title')}>
          {items.map((p) => (
            <li key={p.claimPaymentId}>
              <span className={cx(styles.paymentText)}>
                <span>
                  {t('file.money.payment', {
                    type: p.paymentType
                      ? t(`builder.paymentTypes.${p.paymentType}`, { defaultValue: p.paymentType })
                      : t('file.money.paymentGeneric'),
                  })}
                </span>
                {p.maskedAccount ? (
                  <span className={cx(styles.paymentMeta)}>{p.maskedAccount}</span>
                ) : null}
              </span>
              <span className={cx(styles.paymentAmount)}>
                <span className="ds-num">{fmt.money(p.amount)}</span>
                <PaymentStatusPill status={p.status} />
              </span>
            </li>
          ))}
        </ul>
      ) : payments.isSuccess ? (
        <p className={cx(styles.muted)}>{t('payments.emptyTitle')}</p>
      ) : null}
      {open && (onNewPayment || onRelease) ? (
        <div className={cx(styles.moneyActions)}>
          {onRelease && pending > 0 ? (
            <Button variant="primary" onPress={onRelease}>
              {t('file.money.release', { count: pending })}
            </Button>
          ) : null}
          {onNewPayment ? (
            <Button variant="secondary" onPress={onNewPayment}>
              {t('file.money.newPayment')}
            </Button>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

interface HistoryEvent {
  id: string;
  at: string;
  text: string;
  family: 'success' | 'info' | 'brand' | 'neutral' | 'warning';
}

/** «Ιστορικό»: what the claim, its exposures and its payments record, newest first. */
export function ClaimHistory({ claim }: { claim: ClaimView }) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const payments = usePayments(claim.summary.claimId);
  const items = (payments.data?.items ?? []) as ClaimPaymentView[];
  const s = claim.summary;

  const events: HistoryEvent[] = [
    {
      id: 'created',
      at: s.createdAt,
      text: t('file.history.created', { number: s.claimNumber }),
      family: 'info' as const,
    },
    ...claim.exposures.map((e) => ({
      id: `exp-${e.exposureId}`,
      at: e.createdAt,
      text: t('file.history.exposure', { number: e.exposureNumber, coverage: e.coverageCode }),
      family: 'brand' as const,
    })),
    ...items.flatMap((p) => {
      const amount = fmt.money(p.amount);
      const out: HistoryEvent[] = [];
      if (p.createdAt)
        out.push({
          id: `pay-c-${p.claimPaymentId}`,
          at: p.createdAt,
          text: t('file.history.paymentCreated', { amount }),
          family: 'neutral',
        });
      if (p.issuedAt)
        out.push({
          id: `pay-i-${p.claimPaymentId}`,
          at: p.issuedAt,
          text: t('file.history.paymentIssued', { amount }),
          family: 'success',
        });
      if (p.clearedAt)
        out.push({
          id: `pay-k-${p.claimPaymentId}`,
          at: p.clearedAt,
          text: t('file.history.paymentCleared', { amount }),
          family: 'success',
        });
      return out;
    }),
    ...(s.closedAt
      ? [
          {
            id: 'closed',
            at: s.closedAt,
            text: s.outcome
              ? t('file.history.closedWith', { outcome: t(`codes.outcome.${s.outcome}`) })
              : t('file.history.closed'),
            family: 'warning' as const,
          },
        ]
      : []),
  ]
    // Newest first; events at the same instant keep their causal order reversed (closed above paid above created).
    .map((event, index) => ({ event, index }))
    .sort((a, b) => b.event.at.localeCompare(a.event.at) || b.index - a.index)
    .map(({ event }) => event);

  return (
    <ol className={cx(styles.timeline)}>
      {events.map((e) => (
        <li key={e.id} data-family={e.family}>
          <span className={cx(styles.eventText)}>{e.text}</span>
          <span className={cx(styles.eventMeta)}>{fmt.dateTime(e.at)}</span>
        </li>
      ))}
    </ol>
  );
}
