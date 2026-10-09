import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import {
  Banner,
  Button,
  Checkbox,
  KeyValueList,
  TextField,
  announce,
  type KeyValueItem,
} from '../../../design-system';
import { currencyFractionDigits, fromMinor, toMinor } from '../../../format';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  decideRefund,
  resubmitRefund,
  useRefund,
  useRefundMutation,
  type PayeeAccountCreateResponse,
  type RefundDecideRequest,
  type RefundDecideResponse,
  type RefundResubmitRequest,
  type RefundView,
} from './api';
import { RefundPayeeForm } from './RefundPayeeForm';
import { BreakdownTable, NettingTable, RefundStatePill } from './RefundParts';
import { canOfferDecision, currentRoles, refundDecideRole, refundViewRoles } from './roles';

function DecisionForm({
  refund,
  onDone,
}: {
  refund: RefundView;
  onDone: (response: RefundDecideResponse) => void;
}) {
  const { t } = useTranslation('billing');
  const { keyFor, release } = useIdempotencyKey();
  const [comment, setComment] = useState('');
  const [intent, setIntent] = useState<'APPROVE' | 'REJECT' | null>(null);
  const [tried, setTried] = useState(false);
  const mutation = useRefundMutation(decideRefund, (response) => {
    release();
    onDone(response);
    announce(
      response.refund.approvalState === 'REJECTED'
        ? t('refunds.decision.rejected')
        : t('refunds.decision.approved'),
    );
  });

  const decide = (decision: 'APPROVE' | 'REJECT') => {
    setIntent(decision);
    setTried(true);
    if (decision === 'REJECT' && comment.trim() === '') return;
    // The refund id and the decision only: the required authority and the decider come from the server.
    const request: RefundDecideRequest = {
      refundId: refund.refundId,
      decision,
      ...(comment.trim() ? { comment: comment.trim() } : {}),
    };
    mutation.mutate({ request, key: keyFor(request) });
  };

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('refunds.decision.title')}
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      <TextField
        label={t('refunds.decision.comment')}
        multiline
        maxLength={1000}
        helperText={t('refunds.decision.commentHelp')}
        value={comment}
        onChange={setComment}
        errorMessage={
          tried && intent === 'REJECT' && comment.trim() === ''
            ? t('refunds.decision.commentRequired')
            : undefined
        }
      />
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('refunds.decision.failed')} />
      ) : null}
      <div className={styles.actions}>
        <Button
          variant="primary"
          isLoading={mutation.isPending && intent === 'APPROVE'}
          isDisabled={mutation.isPending}
          onPress={() => {
            decide('APPROVE');
          }}
        >
          {t('refunds.decision.approve')}
        </Button>
        <Button
          variant="danger"
          isLoading={mutation.isPending && intent === 'REJECT'}
          isDisabled={mutation.isPending}
          onPress={() => {
            decide('REJECT');
          }}
        >
          {t('refunds.decision.reject')}
        </Button>
      </div>
    </form>
  );
}

function ResubmitForm({ refund, onDone }: { refund: RefundView; onDone: () => void }) {
  const { t } = useTranslation('billing');
  const { keyFor, release } = useIdempotencyKey();
  const [comment, setComment] = useState('');
  const [different, setDifferent] = useState(false);
  const [newPayeeId, setNewPayeeId] = useState<string | null>(null);
  const [needPayee, setNeedPayee] = useState(false);
  const mutation = useRefundMutation(resubmitRefund, () => {
    release();
    onDone();
    announce(t('refunds.resubmit.done'));
  });

  const submit = () => {
    if (different && newPayeeId === null) {
      setNeedPayee(true);
      return;
    }
    setNeedPayee(false);
    const request: RefundResubmitRequest = {
      refundId: refund.refundId,
      ...(different && newPayeeId ? { payeeAccountId: newPayeeId } : {}),
      ...(comment.trim() ? { comment: comment.trim() } : {}),
    };
    mutation.mutate({ request, key: keyFor(request) });
  };

  return (
    <div className={styles.stack}>
      <p className={styles.muted}>{t('refunds.resubmit.intro')}</p>
      <TextField
        label={t('refunds.resubmit.comment')}
        multiline
        maxLength={1000}
        value={comment}
        onChange={setComment}
      />
      <Checkbox
        isSelected={different}
        onChange={(selected) => {
          setDifferent(selected);
          if (!selected) setNewPayeeId(null);
        }}
      >
        {t('refunds.resubmit.differentPayee')}
      </Checkbox>
      {different ? (
        <RefundPayeeForm
          partyId={refund.payee.payeePartyId}
          onSaved={(account: PayeeAccountCreateResponse) => {
            setNewPayeeId(account.payeeAccountId);
            setNeedPayee(false);
          }}
        />
      ) : null}
      {needPayee ? (
        <Banner variant="warning" live="alert" title={t('refunds.resubmit.needPayee')} />
      ) : null}
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('refunds.resubmit.failed')} />
      ) : null}
      <div className={styles.actions}>
        <Button variant="primary" isLoading={mutation.isPending} onPress={submit}>
          {t('refunds.resubmit.submit')}
        </Button>
      </div>
    </div>
  );
}

const decisionIds = ['decidedBy', 'decidedAt', 'comment', 'disbursement'];

function RefundDetails({ refund }: { refund: RefundView }) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const currency = refund.amount.currency;
  const roles = currentRoles();
  const offerDecision = refund.state === 'PENDING_APPROVAL' && canOfferDecision(refund.requestedBy);
  const offerResubmit =
    (refund.state === 'REJECTED' || refund.state === 'RETURNED') &&
    roles.some((r) => refundViewRoles.includes(r));
  const isMaker = refund.state === 'PENDING_APPROVAL' && !offerDecision;
  // The outcome stays on screen after the refetch moves the refund out of its pending state.
  const [outcome, setOutcome] = useState<RefundDecideResponse | null>(null);
  const [resubmitted, setResubmitted] = useState(false);
  const decided = refund.decidedAt !== undefined || refund.disbursementId !== undefined;
  const fractionDigits = currencyFractionDigits(currency);
  const nettedTotal = refund.netting.reduce(
    (sum, l) => sum + toMinor(l.amount.amount, fractionDigits),
    0n,
  );

  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('overline')}
        title={t('refunds.detail.title')}
        subtitle={<RefundStatePill state={refund.state} />}
        actions={
          <>
            <LinkButton variant="secondary" to={`/billing/accounts/${refund.billingAccountId}`}>
              {t('refunds.detail.openAccount')}
            </LinkButton>
            <LinkButton variant="secondary" to="/billing/refunds">
              {t('refunds.backToInbox')}
            </LinkButton>
          </>
        }
      />
      <div className={styles.grid}>
        <Section title={t('refunds.detail.summary')}>
          <KeyValueList
            aria-label={t('refunds.detail.summary')}
            items={(
              [
                {
                  id: 'amount',
                  label: t('refunds.detail.amount'),
                  value: fmt.money(refund.amount),
                  kind: 'money',
                },
                {
                  id: 'approval',
                  label: t('refunds.detail.approvalState'),
                  value: t(`refunds.approval.${refund.approvalState}`),
                },
                {
                  id: 'reason',
                  label: t('refunds.detail.reason'),
                  value: refund.reasonCode
                    ? t(`refunds.propose.reasons.${refund.reasonCode}`, {
                        defaultValue: refund.reasonCode,
                      })
                    : null,
                },
                {
                  id: 'method',
                  label: t('refunds.detail.payoutMethod'),
                  value: t(`refunds.payoutMethod.${refund.payoutMethod}`, {
                    defaultValue: refund.payoutMethod,
                  }),
                  kind: 'mono',
                },
                {
                  id: 'requestedBy',
                  label: t('refunds.detail.requestedBy'),
                  value: refund.requestedBy,
                },
                {
                  id: 'proposedAt',
                  label: t('refunds.detail.proposedAt'),
                  value: fmt.dateTime(refund.proposedAt),
                },
                {
                  id: 'decidedBy',
                  label: t('refunds.detail.decidedBy'),
                  value: refund.decidedBy ?? null,
                },
                {
                  id: 'decidedAt',
                  label: t('refunds.detail.decidedAt'),
                  value: refund.decidedAt ? fmt.dateTime(refund.decidedAt) : null,
                },
                {
                  id: 'comment',
                  label: t('refunds.detail.decisionComment'),
                  value: refund.decisionComment ?? null,
                },
                {
                  id: 'disbursement',
                  label: t('refunds.detail.disbursement'),
                  value: refund.disbursementId ?? null,
                  kind: 'mono',
                },
              ] satisfies KeyValueItem[]
            ).filter((item) => decided || !decisionIds.includes(item.id))}
          />
        </Section>
        <Section title={t('refunds.detail.payee')}>
          <p className={styles.muted}>{t('refunds.detail.payeeNote')}</p>
          <KeyValueList
            aria-label={t('refunds.detail.payee')}
            items={[
              {
                id: 'iban',
                label: t('refunds.detail.maskedIban'),
                value: refund.payee.maskedIban,
                kind: 'mono',
              },
              {
                id: 'verification',
                label: t('refunds.detail.verification'),
                value: t(`refunds.verificationStatus.${refund.payee.verificationStatus}`, {
                  defaultValue: refund.payee.verificationStatus,
                }),
              },
            ]}
          />
        </Section>
      </div>
      <Section title={t('refunds.breakdown.title')}>
        <BreakdownTable lines={refund.breakdown} currency={currency} />
      </Section>
      <Section title={t('refunds.netting.title')}>
        <NettingTable lines={refund.netting} currency={currency} />
        {refund.netting.length > 0 ? (
          <p className={styles.muted}>
            {t('refunds.netting.total')}:{' '}
            {fmt.money({ amount: fromMinor(nettedTotal, fractionDigits), currency })}
          </p>
        ) : null}
      </Section>
      {outcome ? (
        <Section title={t('refunds.decision.title')} family="plum">
          <Banner
            variant={outcome.refund.approvalState === 'REJECTED' ? 'info' : 'success'}
            live="status"
            title={
              outcome.refund.approvalState === 'REJECTED'
                ? t('refunds.decision.rejected')
                : t('refunds.decision.approved')
            }
          >
            <p>{t('refunds.decision.decidedBody')}</p>
            {outcome.disbursementId ? (
              <KeyValueList
                aria-label={t('refunds.decision.disbursementCreated')}
                items={[
                  {
                    id: 'disbursement',
                    label: t('refunds.decision.disbursementCreated'),
                    value: outcome.disbursementId,
                    kind: 'mono',
                  },
                ]}
              />
            ) : null}
          </Banner>
        </Section>
      ) : refund.state === 'PENDING_APPROVAL' ? (
        <Section title={t('refunds.decision.title')} family="plum">
          {offerDecision ? (
            <DecisionForm refund={refund} onDone={setOutcome} />
          ) : (
            <Banner variant="info" live="none" title={t('refunds.decision.title')}>
              {isMaker && roles.includes(refundDecideRole)
                ? t('refunds.decision.makerNote')
                : t('refunds.decision.roleNote')}
            </Banner>
          )}
        </Section>
      ) : null}
      {resubmitted ? (
        <Section title={t('refunds.resubmit.title')}>
          <Banner variant="success" live="status" title={t('refunds.resubmit.done')} />
        </Section>
      ) : offerResubmit ? (
        <Section title={t('refunds.resubmit.title')}>
          <ResubmitForm
            refund={refund}
            onDone={() => {
              setResubmitted(true);
            }}
          />
        </Section>
      ) : null}
    </div>
  );
}

/** One refund: summary, masked payee, per charge type breakdown, netting and (for an eligible approver) the decision. */
export function RefundDetailPage() {
  const { refundId = '' } = useParams();
  const { t } = useTranslation('billing');
  const query = useRefund(refundId);
  return (
    <QueryView query={query} notFoundMessage={t('refunds.notFound')}>
      {(data) => <RefundDetails refund={data.refund} />}
    </QueryView>
  );
}
