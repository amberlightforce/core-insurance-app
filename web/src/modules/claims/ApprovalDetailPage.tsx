import { useMutation } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { isApiError } from '../../api/client';
import { useIdempotencyKey } from '../../api/idempotency';
import type {
  ApprovalDecideRequest,
  ApprovalDecisionView,
  ApprovalGetResponse,
  ApprovalView,
} from '../../api/types';
import { Banner, Button, KeyValueList, StatusPill, TextField, announce } from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { ProblemBanner } from '../staff/ProblemBanner';
import { QueryView } from '../staff/QueryView';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { decideApproval, useApproval, useRefreshApprovals } from './api';
import { approvalTypeKey, diffRows } from './approvalFormat';

function ApprovalStatusPill({ status }: { status: ApprovalView['status'] }) {
  const { t } = useTranslation('claims');
  if (status === 'PendingApproval')
    return <StatusPill entity="transactionSet" state="pendingApproval" announceChanges={false} />;
  if (status === 'Approved')
    return <StatusPill entity="transactionSet" state="approved" announceChanges={false} />;
  if (status === 'Rejected')
    return <StatusPill entity="transactionSet" state="rejected" announceChanges={false} />;
  return (
    <StatusPill
      semantic="info"
      subLabel={t('approvals.status.Withdrawn')}
      announceChanges={false}
    />
  );
}

function DecisionForm({ request }: { request: ApprovalView }) {
  const { t } = useTranslation('claims');
  const { keyFor, release } = useIdempotencyKey();
  const refresh = useRefreshApprovals();
  const [comment, setComment] = useState('');
  const [intent, setIntent] = useState<'Approve' | 'Reject' | null>(null);
  const [tried, setTried] = useState(false);
  const [done, setDone] = useState<ApprovalDecisionView | null>(null);

  const mutation = useMutation({
    mutationFn: ({ body, key }: { body: ApprovalDecideRequest; key: string }) =>
      decideApproval(body, key),
    onSuccess: (response) => {
      release();
      setDone(response.decision);
      announce(
        response.decision.decision === 'Approved'
          ? t('approvals.approved')
          : t('approvals.rejected'),
      );
      void refresh();
    },
    onError: (error) => {
      // The request changed under the checker (hash or state): reload what they are looking at.
      if (isApiError(error) && error.code === 'PLT-ERR-APPROVAL-STALE') void refresh();
    },
  });
  const stale = isApiError(mutation.error) && mutation.error.code === 'PLT-ERR-APPROVAL-STALE';

  const decide = (decision: 'Approve' | 'Reject') => {
    setIntent(decision);
    setTried(true);
    if (decision === 'Reject' && comment.trim() === '') return;
    const body: ApprovalDecideRequest = {
      requestId: request.requestId,
      decision,
      // The hash of the content shown on this page: the server refuses the decision if it no longer matches.
      payloadHash: request.payloadHash,
      ...(comment.trim() ? { comment: comment.trim() } : {}),
    };
    mutation.mutate({ body, key: keyFor(body) });
  };

  if (done) {
    return (
      <Banner
        variant={done.decision === 'Approved' ? 'success' : 'info'}
        live="status"
        title={done.decision === 'Approved' ? t('approvals.approved') : t('approvals.rejected')}
      >
        {t('approvals.decidedBody')}
      </Banner>
    );
  }

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('approvals.decision')}
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      <TextField
        label={t('approvals.comment')}
        multiline
        maxLength={1000}
        helperText={t('approvals.commentHelp')}
        value={comment}
        onChange={setComment}
        errorMessage={
          tried && intent === 'Reject' && comment.trim() === ''
            ? t('approvals.commentRequired')
            : undefined
        }
      />
      {stale ? (
        <Banner variant="warning" live="alert" title={t('approvals.stale.title')}>
          {t('approvals.stale.body')}
        </Banner>
      ) : mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('approvals.failed')} />
      ) : null}
      <div className={styles.actions}>
        <Button
          variant="primary"
          isLoading={mutation.isPending && intent === 'Approve'}
          isDisabled={mutation.isPending}
          onPress={() => {
            decide('Approve');
          }}
        >
          {t('approvals.approve')}
        </Button>
        <Button
          variant="danger"
          isLoading={mutation.isPending && intent === 'Reject'}
          isDisabled={mutation.isPending}
          onPress={() => {
            decide('Reject');
          }}
        >
          {t('approvals.reject')}
        </Button>
      </div>
    </form>
  );
}

function ApprovalDetails({ data }: { data: ApprovalGetResponse }) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const { request, decision } = data;
  const rows = useMemo(
    () =>
      diffRows(request.diff, (m) => fmt.money(m), {
        yes: t('approvals.diff.yes'),
        no: t('approvals.diff.no'),
      }),
    [request.diff, fmt, t],
  );
  const pending = request.status === 'PendingApproval';

  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('overline')}
        title={t('approvals.detailTitle', {
          type: t(approvalTypeKey(request.type), { defaultValue: request.type }),
        })}
        subtitle={<ApprovalStatusPill status={request.status} />}
        actions={
          <LinkButton variant="secondary" to="/claims/approvals">
            {t('approvals.backToInbox')}
          </LinkButton>
        }
      />
      <div className={styles.grid}>
        <Section title={t('approvals.summary')}>
          <KeyValueList
            aria-label={t('approvals.summary')}
            items={[
              {
                id: 'type',
                label: t('approvals.columns.type'),
                value: t(approvalTypeKey(request.type), { defaultValue: request.type }),
              },
              {
                id: 'subject',
                label: t('approvals.columns.subject'),
                value: `${request.objectRef.module} · ${request.objectRef.type}`,
                kind: 'mono',
              },
              {
                id: 'subjectId',
                label: t('approvals.subjectId'),
                value: request.objectRef.id,
                kind: 'mono',
              },
              {
                id: 'amount',
                label: t('approvals.columns.amount'),
                value: request.authority.amount ? fmt.money(request.authority.amount) : null,
                kind: 'money',
              },
              { id: 'maker', label: t('approvals.columns.maker'), value: request.maker.id },
              {
                id: 'requested',
                label: t('approvals.columns.requestedAt'),
                value: fmt.dateTime(request.requestedAt),
              },
              {
                id: 'role',
                label: t('approvals.referralRole'),
                value: request.referralRole,
                kind: 'mono',
              },
              { id: 'reason', label: t('approvals.columns.reason'), value: request.reason ?? null },
              {
                id: 'hash',
                label: t('approvals.payloadHash'),
                value: request.payloadHash,
                kind: 'mono',
              },
            ]}
          />
        </Section>
        <Section title={t('approvals.diff.title')}>
          {rows.length === 0 ? (
            <p className={styles.muted}>{t('approvals.diff.none')}</p>
          ) : (
            <KeyValueList
              aria-label={t('approvals.diff.title')}
              items={rows.map((row) => ({
                id: row.id,
                label: row.label,
                value:
                  row.value ??
                  t('approvals.diff.change', { from: row.from ?? '—', to: row.to ?? '—' }),
              }))}
            />
          )}
        </Section>
      </div>
      {decision ? (
        <Section title={t('approvals.decisionRecorded')}>
          <KeyValueList
            aria-label={t('approvals.decisionRecorded')}
            items={[
              {
                id: 'decision',
                label: t('approvals.decision'),
                value:
                  decision.decision === 'Approved'
                    ? t('approvals.status.Approved')
                    : t('approvals.status.Rejected'),
              },
              { id: 'checker', label: t('approvals.checker'), value: decision.checker.id },
              {
                id: 'at',
                label: t('approvals.decidedAt'),
                value: fmt.dateTime(decision.decidedAt),
              },
              { id: 'comment', label: t('approvals.comment'), value: decision.comment ?? null },
            ]}
          />
        </Section>
      ) : null}
      {pending ? (
        <Section title={t('approvals.decision')}>
          <DecisionForm request={request} />
        </Section>
      ) : (
        <Banner variant="info" live="none" title={t('approvals.notPending')}>
          {t('approvals.notPendingBody')}
        </Banner>
      )}
    </div>
  );
}

/** One approval request: type, subject, amount, maker, reason and diff, with approve / reject (plt.Approval.decide). */
export function ApprovalDetailPage() {
  const { t } = useTranslation('claims');
  const { requestId = '' } = useParams();
  const query = useApproval(requestId);
  return (
    <div className={styles.page}>
      <QueryView query={query} notFoundMessage={t('approvals.notFound')}>
        {(data) => <ApprovalDetails data={data} />}
      </QueryView>
    </div>
  );
}
