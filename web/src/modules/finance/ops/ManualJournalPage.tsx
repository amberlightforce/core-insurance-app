import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import {
  Banner,
  Button,
  KeyValueList,
  TextField,
  announce,
  moneyColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  useDecideManualJournal,
  useManualJournal,
  useSubmitManualJournal,
  useWithdrawManualJournal,
} from './api';
import { isMaker, lineTotals } from './journalMath';
import { ManualJournalStatusPill } from './ManualJournalParts';
import type { ManualJournal, ManualJournalLine } from './types';

function Decision({ journal, onDone }: { journal: ManualJournal; onDone: () => void }) {
  const { t } = useTranslation('finance');
  const { keyFor, release } = useIdempotencyKey();
  const [reason, setReason] = useState('');
  const [intent, setIntent] = useState<'APPROVE' | 'REJECT' | null>(null);
  const [tried, setTried] = useState(false);
  const decide = useDecideManualJournal((response) => {
    release();
    announce(
      response.manualJournal.status === 'REJECTED'
        ? t('ops.manual.decision.rejected')
        : t('ops.manual.decision.approved'),
    );
    onDone();
  });
  const withdraw = useWithdrawManualJournal(() => {
    announce(t('ops.manual.decision.withdrawn'));
    onDone();
  });
  // The maker never decides their own journal. This only hides the control; the server answers 403 PLT-ERR-SOD.
  const maker = isMaker(journal.submittedBy ?? journal.createdBy);

  const submit = (decision: 'APPROVE' | 'REJECT') => {
    setIntent(decision);
    setTried(true);
    if (decision === 'REJECT' && reason.trim() === '') return;
    const request = {
      id: journal.id,
      decision,
      ...(reason.trim() ? { comment: reason.trim() } : {}),
    };
    decide.mutate({ request, key: keyFor(request) });
  };

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('ops.manual.decision.title')}
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      {maker ? (
        <Banner variant="info" live="none" title={t('ops.manual.decision.makerTitle')}>
          {t('ops.manual.decision.makerBody')}
        </Banner>
      ) : null}
      <TextField
        label={t('ops.manual.decision.reason')}
        multiline
        maxLength={1000}
        helperText={t('ops.manual.decision.reasonHelp')}
        isDisabled={maker}
        value={reason}
        onChange={setReason}
        errorMessage={
          tried && intent === 'REJECT' && reason.trim() === ''
            ? t('ops.manual.decision.reasonRequired')
            : undefined
        }
      />
      {decide.isError ? (
        <ProblemBanner error={decide.error} title={t('ops.manual.decision.failed')} />
      ) : null}
      {withdraw.isError ? (
        <ProblemBanner error={withdraw.error} title={t('ops.manual.decision.withdrawFailed')} />
      ) : null}
      <div className={styles.actions}>
        <Button
          variant="primary"
          isDisabled={maker || decide.isPending}
          isLoading={decide.isPending && intent === 'APPROVE'}
          onPress={() => {
            submit('APPROVE');
          }}
        >
          {t('ops.manual.decision.approve')}
        </Button>
        <Button
          variant="danger"
          isDisabled={maker || decide.isPending}
          isLoading={decide.isPending && intent === 'REJECT'}
          onPress={() => {
            submit('REJECT');
          }}
        >
          {t('ops.manual.decision.reject')}
        </Button>
        {maker ? (
          <Button
            variant="secondary"
            isLoading={withdraw.isPending}
            onPress={() => {
              withdraw.mutate({ id: journal.id, key: crypto.randomUUID() });
            }}
          >
            {t('ops.manual.decision.withdraw')}
          </Button>
        ) : null}
      </div>
    </form>
  );
}

function DraftActions({ journal, onDone }: { journal: ManualJournal; onDone: () => void }) {
  const { t } = useTranslation('finance');
  const { keyFor, release } = useIdempotencyKey();
  const withdraw = useWithdrawManualJournal(() => {
    release();
    announce(t('ops.manual.decision.withdrawn'));
    onDone();
  });
  const submit = useSubmitManualJournal(() => {
    release();
    announce(t('ops.manual.decision.submitted'));
    onDone();
  });
  // Only the maker can submit or withdraw; the server answers 403 FIN-ERR-SOD otherwise.
  const maker = isMaker(journal.createdBy);
  return (
    <div className={styles.stack}>
      <Banner variant="info" live="none" title={t('ops.manual.form.draftNote')} />
      {withdraw.isError ? (
        <ProblemBanner error={withdraw.error} title={t('ops.manual.decision.withdrawFailed')} />
      ) : null}
      {submit.isError ? (
        <ProblemBanner error={submit.error} title={t('ops.manual.decision.submitFailed')} />
      ) : null}
      {maker ? (
        <div className={styles.actions}>
          <Button
            variant="primary"
            isLoading={submit.isPending}
            isDisabled={withdraw.isPending}
            onPress={() => {
              const request = { id: journal.id };
              submit.mutate({ request, key: keyFor(request) });
            }}
          >
            {t('ops.manual.decision.submitForApproval')}
          </Button>
          <Button
            variant="secondary"
            isLoading={withdraw.isPending}
            isDisabled={submit.isPending}
            onPress={() => {
              const request = { id: journal.id };
              withdraw.mutate({ id: journal.id, key: keyFor(request) });
            }}
          >
            {t('ops.manual.decision.withdraw')}
          </Button>
        </div>
      ) : null}
    </div>
  );
}

function Details({
  journal,
  postedIds,
  reload,
}: {
  journal: ManualJournal;
  postedIds: string[];
  reload: () => void;
}) {
  const { t } = useTranslation('finance');
  const fmt = useFormat();
  const totals = lineTotals(journal.lines);
  const currency = journal.currency;
  const columns: DataColumn<ManualJournalLine>[] = [
    textColumn<ManualJournalLine>('account', t('ops.manual.form.account'), (l) => l.account, {
      size: 200,
    }),
    moneyColumn<ManualJournalLine>(
      'debit',
      t('lines.debit'),
      (l) => (l.side === 'DEBIT' ? l.amount : null),
      { currency },
    ),
    moneyColumn<ManualJournalLine>(
      'credit',
      t('lines.credit'),
      (l) => (l.side === 'CREDIT' ? l.amount : null),
      { currency },
    ),
  ];
  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('overline')}
        title={t('ops.manual.detailTitle')}
        {...(journal.number ? { recordId: journal.number } : {})}
        subtitle={<ManualJournalStatusPill status={journal.status} />}
        actions={
          <LinkButton variant="secondary" to="/finance/manual-journals">
            {t('ops.manual.backToList')}
          </LinkButton>
        }
      />
      <div className={styles.grid}>
        <Section family="brand" title={t('ops.manual.summary')}>
          <KeyValueList
            aria-label={t('ops.manual.summary')}
            items={[
              { id: 'description', label: t('ops.manual.description'), value: journal.description },
              { id: 'book', label: t('ops.manual.book'), value: journal.book, kind: 'mono' },
              {
                id: 'date',
                label: t('ops.manual.accountingDate'),
                value: fmt.date(journal.accountingDate),
              },
              {
                id: 'evidence',
                label: t('ops.manual.evidence'),
                value: journal.evidenceRef,
                kind: 'mono',
              },
              {
                id: 'by',
                label: t('ops.manual.submittedBy'),
                value: journal.submittedBy ?? journal.createdBy ?? null,
              },
              {
                id: 'at',
                label: t('ops.manual.submittedAt'),
                value: journal.submittedAt ? fmt.dateTime(journal.submittedAt) : null,
              },
              {
                id: 'decidedBy',
                label: t('ops.manual.decidedBy'),
                value: journal.decidedBy ?? null,
              },
              {
                id: 'decisionReason',
                label: t('ops.manual.decisionReason'),
                value: journal.decisionReason ?? null,
              },
              {
                id: 'posted',
                label: t('ops.manual.postedIds'),
                value: postedIds.length > 0 ? postedIds.join(', ') : null,
                kind: 'mono',
              },
            ]}
          />
        </Section>
        <Section family="teal" title={t('ops.manual.form.lines')}>
          <SimpleTable<ManualJournalLine>
            aria-label={t('ops.manual.form.lines')}
            columns={columns}
            data={journal.lines}
            getRowId={(l) => `${l.account}:${l.side}:${l.amount}`}
          />
          <p className={styles.muted}>
            {t('ops.manual.form.totals', {
              debit: fmt.money({ amount: totals.debit, currency }),
              credit: fmt.money({ amount: totals.credit, currency }),
            })}
          </p>
        </Section>
      </div>
      {journal.status === 'DRAFT' ? (
        <Section family="info" title={t('ops.manual.statusLabel')}>
          <DraftActions journal={journal} onDone={reload} />
        </Section>
      ) : null}
      {journal.status === 'SUBMITTED' ? (
        <Section title={t('ops.manual.decision.title')} family="plum">
          <Decision journal={journal} onDone={reload} />
        </Section>
      ) : null}
    </div>
  );
}

/** One manual journal with its lines, and the second user's decision while it waits (four-eyes). */
export function ManualJournalPage() {
  const { id = '' } = useParams();
  const { t } = useTranslation('finance');
  const query = useManualJournal(id);
  return (
    <QueryView query={query} notFoundMessage={t('ops.manual.notFound')}>
      {(data) => (
        <Details
          journal={data.manualJournal}
          postedIds={data.postedIds ?? []}
          reload={() => {
            void query.refetch();
          }}
        />
      )}
    </QueryView>
  );
}
