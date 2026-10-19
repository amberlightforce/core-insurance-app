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
  identifierColumn,
  moneyColumn,
  type DataColumn,
} from '../../../design-system';
import { currencyFractionDigits, fromMinor, toMinor } from '../../../format';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  useApproveRelease,
  useDisbursement,
  type DisbursementApproveReleaseRequest,
  type DisbursementApproveReleaseResponse,
  type DisbursementView,
} from './api';
import { CounterpartyName } from './ReceivableParts';
import { DisbursementStatusPill } from './ReleaseParts';
import { canApproveReleases, isRequester } from './roles';

type Line = NonNullable<DisbursementView['lines']>[number];

function Decision({
  disbursement,
  onDone,
}: {
  disbursement: DisbursementView;
  onDone: (r: DisbursementApproveReleaseResponse) => void;
}) {
  const { t } = useTranslation('billing');
  const { keyFor, release } = useIdempotencyKey();
  const [reason, setReason] = useState('');
  const [intent, setIntent] = useState<'APPROVE' | 'REJECT' | null>(null);
  const [tried, setTried] = useState(false);
  const mutation = useApproveRelease((response) => {
    release();
    onDone(response);
    announce(
      response.decision === 'REJECT'
        ? t('releases.decision.rejected')
        : t('releases.decision.approved'),
    );
  });

  const requester = isRequester(disbursement.releaseApproval?.requestedBy);
  const allowed = canApproveReleases() && !requester;

  const decide = (decision: 'APPROVE' | 'REJECT') => {
    setIntent(decision);
    setTried(true);
    if (decision === 'REJECT' && reason.trim() === '') return;
    const request: DisbursementApproveReleaseRequest = {
      disbursementId: disbursement.disbursementId,
      decision,
      ...(reason.trim() ? { reason: reason.trim() } : {}),
    };
    mutation.mutate({ request, key: keyFor(request) });
  };

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('releases.decision.title')}
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      {!allowed ? (
        <Banner variant="info" live="none" title={t('releases.decision.title')}>
          {requester ? t('releases.decision.sod') : t('releases.decision.roleNote')}
        </Banner>
      ) : null}
      <TextField
        label={t('releases.decision.reason')}
        multiline
        maxLength={1000}
        helperText={t('releases.decision.reasonHelp')}
        isDisabled={!allowed}
        value={reason}
        onChange={setReason}
        errorMessage={
          tried && intent === 'REJECT' && reason.trim() === ''
            ? t('releases.decision.reasonRequired')
            : undefined
        }
      />
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('releases.decision.failed')} />
      ) : null}
      <div className={styles.actions}>
        <Button
          variant="primary"
          isDisabled={!allowed || mutation.isPending}
          isLoading={mutation.isPending && intent === 'APPROVE'}
          onPress={() => {
            decide('APPROVE');
          }}
        >
          {t('releases.decision.approve')}
        </Button>
        <Button
          variant="danger"
          isDisabled={!allowed || mutation.isPending}
          isLoading={mutation.isPending && intent === 'REJECT'}
          onPress={() => {
            decide('REJECT');
          }}
        >
          {t('releases.decision.reject')}
        </Button>
      </div>
    </form>
  );
}

function ReleaseDetails({ disbursement }: { disbursement: DisbursementView }) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const [outcome, setOutcome] = useState<DisbursementApproveReleaseResponse | null>(null);
  const currency = disbursement.amount.currency;
  const digits = currencyFractionDigits(currency);
  const lines = disbursement.lines ?? [];
  const sumMinor = lines.reduce((s, l) => s + toMinor(l.amount.amount, digits), 0n);
  const sum = { amount: fromMinor(sumMinor, digits), currency };
  const matches = sumMinor === toMinor(disbursement.amount.amount, digits);
  const pending = disbursement.status === 'PendingApproval';
  const isFs = disbursement.sourceType === 'FS_CLEARING';

  const columns: DataColumn<Line>[] = [
    identifierColumn<Line>('claim', t('releases.lines.claim'), (l) => l.claimId),
    identifierColumn<Line>('case', t('releases.lines.case'), (l) => l.fsCaseId),
    moneyColumn<Line>('amount', t('releases.lines.amount'), (l) => l.amount.amount, { currency }),
  ];

  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('overline')}
        title={t('releases.detail.title')}
        {...(disbursement.disbursementNumber ? { recordId: disbursement.disbursementNumber } : {})}
        subtitle={<DisbursementStatusPill status={disbursement.status} />}
        actions={
          <LinkButton variant="secondary" to="/billing/releases">
            {t('releases.backToInbox')}
          </LinkButton>
        }
      />
      <div className={styles.grid}>
        <Section title={t('releases.detail.summary')}>
          <KeyValueList
            aria-label={t('releases.detail.summary')}
            items={[
              {
                id: 'statement',
                label: t('releases.detail.statementRef'),
                value: disbursement.statementReference ?? null,
                kind: 'mono',
              },
              {
                id: 'counterparty',
                label: t('releases.detail.counterparty'),
                value: disbursement.payeePartyId ? (
                  <CounterpartyName partyId={disbursement.payeePartyId} />
                ) : null,
              },
              {
                id: 'net',
                label: t('releases.detail.net'),
                value: fmt.money(disbursement.amount),
                kind: 'money',
              },
              {
                id: 'method',
                label: t('releases.detail.method'),
                value: disbursement.method ?? null,
                kind: 'mono',
              },
              {
                id: 'payee',
                label: t('releases.detail.maskedPayee'),
                value: disbursement.maskedPayeeAccount ?? null,
                kind: 'mono',
              },
              {
                id: 'requestedBy',
                label: t('releases.detail.requestedBy'),
                value: disbursement.releaseApproval?.requestedBy ?? null,
              },
              {
                id: 'requestedAt',
                label: t('releases.detail.requestedAt'),
                value: disbursement.requestedAt ? fmt.dateTime(disbursement.requestedAt) : null,
              },
            ]}
          />
        </Section>
        <Section title={t('releases.detail.checks')}>
          <KeyValueList
            aria-label={t('releases.detail.checks')}
            items={[
              {
                id: 'screening',
                label: t('releases.detail.screening'),
                value: t('releases.detail.stub'),
              },
              {
                id: 'vop',
                label: t('releases.detail.vop'),
                value: t('releases.detail.stub'),
              },
            ]}
          />
          <p className={styles.muted}>{t('releases.detail.checksNote')}</p>
        </Section>
      </div>
      {isFs ? (
        <Section title={t('releases.lines.title')}>
          <SimpleTable<Line>
            aria-label={t('releases.lines.title')}
            columns={columns}
            data={lines}
            getRowId={(l) => `${l.claimId}:${l.fsCaseId}`}
          />
          <p className={styles.muted}>
            {t('releases.lines.sum')}: {fmt.money(sum)}
          </p>
          {matches ? (
            <Banner variant="success" live="none" title={t('releases.lines.matches')} />
          ) : (
            <Banner variant="warning" live="alert" title={t('releases.lines.mismatch')}>
              {t('releases.lines.mismatchBody', {
                sum: fmt.money(sum),
                net: fmt.money(disbursement.amount),
              })}
            </Banner>
          )}
        </Section>
      ) : null}
      {outcome ? (
        <Section title={t('releases.decision.title')} family="plum">
          <Banner
            variant={outcome.decision === 'REJECT' ? 'info' : 'success'}
            live="status"
            title={
              outcome.decision === 'REJECT'
                ? t('releases.decision.rejected')
                : t('releases.decision.approved')
            }
          />
        </Section>
      ) : pending ? (
        <Section title={t('releases.decision.title')} family="plum">
          <Decision disbursement={disbursement} onDone={setOutcome} />
        </Section>
      ) : null}
    </div>
  );
}

/** One FS_CLEARING release: facts, claim-level lines checked against the net, and the decision (Staff.BillingManager). */
export function ReleaseDetailPage() {
  const { disbursementId = '' } = useParams();
  const { t } = useTranslation('billing');
  const query = useDisbursement(disbursementId);
  return (
    <QueryView query={query} notFoundMessage={t('releases.notFound')}>
      {(data) => <ReleaseDetails disbursement={data.disbursement} />}
    </QueryView>
  );
}
