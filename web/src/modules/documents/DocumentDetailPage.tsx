import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { useIdempotencyKey } from '../../api/idempotency';
import {
  Banner,
  Button,
  EmptyState,
  KeyValueList,
  StatusPill,
  TextField,
  announce,
  dateColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { ProblemBanner } from '../staff/ProblemBanner';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import {
  documentSemantic,
  useDocument,
  useEvidencePack,
  useReprint,
  type DeliveryView,
  type DocumentView,
  type EvidenceView,
} from './api';

function DeliveryTable({ items }: { items: DeliveryView[] }) {
  const { t } = useTranslation('documents');
  const columns = useMemo<DataColumn<DeliveryView>[]>(
    () => [
      textColumn<DeliveryView>('channel', t('deliveries.channel'), (d) => d.channel),
      textColumn<DeliveryView>('medium', t('deliveries.medium'), (d) => d.medium),
      statusColumn<DeliveryView>(
        'status',
        t('deliveries.status'),
        (d) => d.status,
        (d) => (
          <StatusPill
            semantic={documentSemantic(d.status)}
            subLabel={d.isReprint ? `${d.status} · ${t('deliveries.reprint')}` : d.status}
            announceChanges={false}
          />
        ),
      ),
      textColumn<DeliveryView>('proof', t('deliveries.proof'), (d) => d.proofLevel ?? '—'),
      dateColumn<DeliveryView>('notified', t('deliveries.notified'), (d) => d.notificationDate),
      textColumn<DeliveryView>('failure', t('deliveries.failure'), (d) => d.failureReason ?? '—'),
    ],
    [t],
  );
  return (
    <SimpleTable<DeliveryView>
      aria-label={t('deliveries.title')}
      columns={columns}
      data={items}
      getRowId={(d) => d.deliveryId}
      getRowLabel={(d) => `${d.channel} ${d.status}`}
      emptyState={
        <EmptyState
          kind="first-use"
          illustration={<></>}
          headingLevel={3}
          headline={t('deliveries.emptyTitle')}
          description={t('deliveries.emptyBody')}
        />
      }
    />
  );
}

function EvidenceTable({ documentId }: { documentId: string }) {
  const { t } = useTranslation('documents');
  const query = useEvidencePack(documentId);
  const columns = useMemo<DataColumn<EvidenceView>[]>(
    () => [
      textColumn<EvidenceView>('provider', t('evidence.provider'), (e) => e.provider),
      textColumn<EvidenceView>('event', t('evidence.event'), (e) => e.eventType),
      textColumn<EvidenceView>('outcome', t('evidence.outcome'), (e) => e.outcome),
      textColumn<EvidenceView>(
        'reference',
        t('evidence.reference'),
        (e) => e.proofReference ?? '—',
      ),
      dateColumn<EvidenceView>('proofDate', t('evidence.proofDate'), (e) => e.proofDate),
      dateColumn<EvidenceView>('received', t('evidence.received'), (e) => e.receivedAt),
    ],
    [t],
  );
  return (
    <QueryView query={query}>
      {(pack) => (
        <SimpleTable<EvidenceView>
          aria-label={t('evidence.title')}
          columns={columns}
          data={pack.sealedArchive.evidence}
          getRowId={(e) => e.evidenceId}
          getRowLabel={(e) => `${e.provider} ${e.eventType}`}
          emptyState={
            <EmptyState
              kind="first-use"
              illustration={<></>}
              headingLevel={3}
              headline={t('evidence.emptyTitle')}
              description={t('evidence.emptyBody')}
            />
          }
        />
      )}
    </QueryView>
  );
}

/** Reprint (doc.Document.reprint): a reason is required; one Idempotency-Key per submission, reused on retry. */
function ReprintForm({ documentId }: { documentId: string }) {
  const { t } = useTranslation('documents');
  const { keyFor, release } = useIdempotencyKey();
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  const [done, setDone] = useState(false);
  const mutation = useReprint(documentId, () => {
    release();
    setDone(true);
    setReason('');
    setTried(false);
    announce(t('reprint.done'));
  });
  const valid = reason.trim().length >= 3;
  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('reprint.title')}
      onSubmit={(event) => {
        event.preventDefault();
        setTried(true);
        setDone(false);
        if (!valid) return;
        mutation.mutate({
          reason: reason.trim(),
          key: keyFor({ documentId, reason: reason.trim() }),
        });
      }}
    >
      <p className={styles.muted}>{t('reprint.lead')}</p>
      <TextField
        label={t('reprint.reason')}
        isRequired
        value={reason}
        onChange={setReason}
        errorMessage={tried && !valid ? t('reprint.reasonInvalid') : undefined}
      />
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('reprint.failed')} />
      ) : null}
      {done ? (
        <Banner variant="success" live="status" title={t('reprint.done')}>
          {t('reprint.doneBody')}
        </Banner>
      ) : null}
      <div className={styles.actions}>
        <Button type="submit" variant="primary" isLoading={mutation.isPending}>
          {t('reprint.submit')}
        </Button>
      </div>
    </form>
  );
}

function DocumentBody({ document }: { document: DocumentView }) {
  const { t } = useTranslation('documents');
  const fmt = useFormat();
  const [module = '', type = '', ...rest] = document.subject.split('/');
  const subjectHref = `/documents/subject/${module}/${encodeURIComponent(type)}/${encodeURIComponent(rest.join('/'))}`;
  return (
    <div className={styles.page}>
      <PageHeader
        title={t('detail.title', { type: document.documentType })}
        overline={t('overline')}
        recordId={document.documentNumber}
        subtitle={
          <StatusPill
            semantic={documentSemantic(document.status)}
            subLabel={document.status}
            announceChanges={false}
          />
        }
        facts={[
          { id: 'created', label: t('detail.created'), value: fmt.dateTime(document.createdAt) },
          { id: 'medium', label: t('detail.medium'), value: document.medium },
          { id: 'language', label: t('detail.language'), value: document.language.toUpperCase() },
        ]}
        actions={
          <LinkButton variant="secondary" to={subjectHref}>
            {t('detail.allForSubject')}
          </LinkButton>
        }
      />
      <div className={styles.split}>
        <div className={styles.stack}>
          <Section title={t('deliveries.title')} count={document.deliveries.length}>
            <DeliveryTable items={document.deliveries} />
          </Section>
          <Section title={t('evidence.title')}>
            <EvidenceTable documentId={document.documentId} />
          </Section>
        </div>
        <div className={styles.stack}>
          <Section title={t('detail.record')}>
            <KeyValueList
              aria-label={t('detail.record')}
              items={[
                {
                  id: 'number',
                  label: t('columns.number'),
                  value: document.documentNumber,
                  kind: 'mono',
                },
                { id: 'type', label: t('columns.type'), value: document.documentType },
                {
                  id: 'subject',
                  label: t('detail.subject'),
                  value: document.subject,
                  kind: 'mono',
                },
                {
                  id: 'request',
                  label: t('detail.request'),
                  value: document.requestNo,
                  kind: 'mono',
                },
                {
                  id: 'archive',
                  label: t('detail.archive'),
                  value: document.archiveId,
                  kind: 'mono',
                },
                { id: 'hash', label: t('detail.hash'), value: document.contentHash, kind: 'mono' },
              ]}
            />
            <p className={styles.muted}>{t('detail.noDownload')}</p>
          </Section>
          <Section title={t('reprint.title')}>
            <ReprintForm documentId={document.documentId} />
          </Section>
        </div>
      </div>
    </div>
  );
}

/** One document: record, deliveries, provider evidence and reprint (doc.Document.get, doc.EvidencePack.get, doc.Document.reprint). */
export function DocumentDetailPage() {
  const { t } = useTranslation('documents');
  const { documentId = '' } = useParams();
  const query = useDocument(documentId);
  return (
    <QueryView query={query} notFoundMessage={t('detail.notFound')}>
      {(document) => <DocumentBody document={document} />}
    </QueryView>
  );
}
