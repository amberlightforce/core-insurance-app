import { useMutation } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { isApiError } from '../../api/client';
import { useIdempotencyKey } from '../../api/idempotency';
import {
  Banner,
  Button,
  EmptyState,
  KeyValueList,
  TextField,
  dateColumn,
  identifierColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import {
  decideIssues,
  useIssueText,
  useReferralQueue,
  useRefreshIssues,
  type UwIssueDecideRequest,
  type UwIssueItem,
} from '../quote/uwIssues';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { ProblemBanner } from '../staff/ProblemBanner';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';

type Decision = UwIssueDecideRequest['decision'];

/** Approve or reject one referral with a mandatory reason (uw.Issue.decide; the server checks SoD and authority). */
function DecisionForm({ issue, onDone }: { issue: UwIssueItem; onDone: (d: Decision) => void }) {
  const { t } = useTranslation('uw');
  const text = useIssueText();
  const fmt = useFormat();
  const { keyFor, release } = useIdempotencyKey();
  const refresh = useRefreshIssues();
  const [reason, setReason] = useState('');
  const [intent, setIntent] = useState<Decision | null>(null);
  const [tried, setTried] = useState(false);
  const described = text(issue);

  const mutation = useMutation({
    mutationFn: ({ body, key }: { body: UwIssueDecideRequest; key: string }) =>
      decideIssues(body, key),
    onSuccess: (_, { body }) => {
      release();
      // The outcome banner of the page is a live region: it announces the decision.
      onDone(body.decision);
      void refresh();
    },
    onError: (error) => {
      // The issue changed under the decider (re-evaluated or decided elsewhere): reload the queue.
      if (isApiError(error) && error.code === 'UW-ERR-STALE') void refresh();
    },
  });

  const decide = (decision: Decision) => {
    setIntent(decision);
    setTried(true);
    if (reason.trim() === '') return;
    const body: UwIssueDecideRequest = {
      issueIds: [issue.id],
      decision,
      reason: reason.trim(),
      expectedRecordVersions: { [issue.id]: issue.recordVersion },
    };
    mutation.mutate({ body, key: keyFor(body) });
  };

  return (
    <Section title={t('referrals.decision', { issue: described.type })} headingLevel={3}>
      <KeyValueList
        aria-label={t('referrals.decision', { issue: described.type })}
        items={[
          { id: 'type', label: t('columns.issue'), value: described.type },
          { id: 'reason', label: t('columns.reason'), value: described.why },
          { id: 'job', label: t('referrals.quote'), value: issue.jobRef, kind: 'mono' },
          { id: 'by', label: t('referrals.raisedBy'), value: issue.raisedBy, kind: 'mono' },
          { id: 'at', label: t('referrals.raisedAt'), value: fmt.dateTime(issue.raisedAt) },
        ]}
      />
      <form
        noValidate
        className={styles.stack}
        aria-label={t('referrals.decision', { issue: described.type })}
        onSubmit={(event) => {
          event.preventDefault();
        }}
      >
        <TextField
          label={t('referrals.reason')}
          multiline
          isRequired
          maxLength={1000}
          helperText={t('referrals.reasonHelp')}
          value={reason}
          onChange={setReason}
          errorMessage={tried && reason.trim() === '' ? t('referrals.reasonRequired') : undefined}
        />
        <p className={styles.muted}>{t('referrals.selfNote')}</p>
        {mutation.isError ? (
          <ProblemBanner error={mutation.error} title={t('referrals.failed')} />
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
            {t('referrals.approve')}
          </Button>
          <Button
            variant="danger"
            isLoading={mutation.isPending && intent === 'REJECT'}
            isDisabled={mutation.isPending}
            onPress={() => {
              decide('REJECT');
            }}
          >
            {t('referrals.reject')}
          </Button>
        </div>
      </form>
    </Section>
  );
}

/**
 * Underwriting referrals (slice of the PRD-04 referral workbench, SCR-UW-03 decision panel in its simplest form): the
 * Open issues of the legal entity, and an approve/reject decision with a required reason. Deciding needs
 * Staff.UnderwritingManager and the UW.ISSUE_APPROVAL grant; whoever quoted or bound the job may not decide (SOD-UW-02).
 */
export function ReferralsPage() {
  const { t } = useTranslation('uw');
  const query = useReferralQueue();
  const text = useIssueText();
  const [selected, setSelected] = useState<UwIssueItem | null>(null);
  const [done, setDone] = useState<Decision | null>(null);

  const columns = useMemo<DataColumn<UwIssueItem>[]>(
    () => [
      textColumn<UwIssueItem>('issue', t('columns.issue'), (i) => text(i).type, { size: 180 }),
      textColumn<UwIssueItem>('reason', t('columns.reason'), (i) => text(i).why, { size: 320 }),
      identifierColumn<UwIssueItem>('job', t('referrals.quote'), (i) => i.jobRef, { size: 300 }),
      textColumn<UwIssueItem>('by', t('referrals.raisedBy'), (i) => i.raisedBy),
      dateColumn<UwIssueItem>('at', t('referrals.raisedAt'), (i) => i.raisedAt),
    ],
    [t, text],
  );

  return (
    <div className={styles.page}>
      <PageHeader
        overline={t('referrals.back')}
        title={t('referrals.title')}
        subtitle={<span className="ds-caption">{t('referrals.subtitle')}</span>}
        actions={
          <LinkButton variant="secondary" to="/policies">
            {t('referrals.back')}
          </LinkButton>
        }
      />
      {done ? (
        <Banner
          variant={done === 'APPROVE' ? 'success' : 'info'}
          live="status"
          title={done === 'APPROVE' ? t('referrals.approved') : t('referrals.rejected')}
        />
      ) : null}
      <Section title={t('referrals.open')}>
        <p className={styles.muted}>{t('referrals.openHint')}</p>
        <QueryView query={query}>
          {(page) => (
            <SimpleTable<UwIssueItem>
              aria-label={t('referrals.table')}
              columns={columns}
              data={page.items}
              getRowId={(i) => i.id}
              getRowLabel={(i) => text(i).type}
              onOpen={(i) => {
                setDone(null);
                setSelected(i);
              }}
              emptyState={
                <EmptyState
                  kind="done"
                  headingLevel={3}
                  headline={t('referrals.emptyTitle')}
                  description={t('referrals.emptyBody')}
                />
              }
            />
          )}
        </QueryView>
      </Section>
      {selected ? (
        <DecisionForm
          key={selected.id}
          issue={selected}
          onDone={(decision) => {
            setDone(decision);
            setSelected(null);
          }}
        />
      ) : null}
    </div>
  );
}
