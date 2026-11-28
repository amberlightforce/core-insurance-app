import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import {
  Banner,
  Button,
  cx,
  identifierColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import staff from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { documentNumber, useFiscalDocument, useQrPayload } from './api';
import { FiscalActionDialog, type FiscalAction } from './FiscalActionDialog';
import styles from './Fiscal.module.css';
import { FiscalStatusPill } from './StatusPills';

interface Line {
  fiscalCategoryKey: string;
  amount: { amount: string; currency: string };
  chargeId?: string;
}

/** One fiscal document: status, channel identifiers (MARK, UID), lines and rejection codes. */
export function FiscalDocumentPage() {
  const { t } = useTranslation('compliance');
  const fmt = useFormat();
  const { id = '' } = useParams();
  const query = useFiscalDocument(id);
  const [action, setAction] = useState<FiscalAction | null>(null);
  const [showQr, setShowQr] = useState(false);
  const qr = useQrPayload(id, showQr);

  const columns: DataColumn<Line>[] = [
    identifierColumn<Line>('category', t('fiscal.lines.category'), (l) => l.fiscalCategoryKey, {
      size: 260,
    }),
    textColumn<Line>('amount', t('fiscal.lines.amount'), (l) => fmt.money(l.amount), { size: 160 }),
    identifierColumn<Line>('charge', t('fiscal.lines.charge'), (l) => l.chargeId ?? '—', {
      size: 300,
    }),
  ];

  return (
    <div className={staff.page}>
      <QueryView query={query} notFoundMessage={t('fiscal.notFound')}>
        {(data) => {
          const { document: doc, identifiers } = data;
          const dash = (v?: string | null) => v ?? '—';
          return (
            <>
              <PageHeader
                overline={t('fiscal.overline')}
                title={t(`fiscal.role.${doc.role}`, { defaultValue: doc.role })}
                recordId={documentNumber(data)}
                subtitle={<FiscalStatusPill status={doc.status} />}
                actions={
                  <>
                    {doc.status === 'REJECTED' ? (
                      <Button
                        variant="primary"
                        onPress={() => {
                          setAction('resubmit');
                        }}
                      >
                        {t('fiscal.actions.resubmit.button')}
                      </Button>
                    ) : null}
                    {doc.status === 'REGISTERED' && doc.role === 'ISSUE' ? (
                      <Button
                        variant="secondary"
                        onPress={() => {
                          setAction('correct');
                        }}
                      >
                        {t('fiscal.actions.correct.button')}
                      </Button>
                    ) : null}
                    {doc.status !== 'CANCELLED' ? (
                      <Button
                        variant="secondary"
                        onPress={() => {
                          setAction('cancel');
                        }}
                      >
                        {t('fiscal.actions.cancel.button')}
                      </Button>
                    ) : null}
                    <LinkButton variant="secondary" to="/compliance/fiscal">
                      {t('fiscal.back')}
                    </LinkButton>
                  </>
                }
                facts={[
                  {
                    id: 'issued',
                    label: t('fiscal.columns.issued'),
                    value: fmt.date(doc.issueDate),
                  },
                  { id: 'total', label: t('fiscal.columns.total'), value: fmt.money(doc.total) },
                  {
                    id: 'revision',
                    label: t('fiscal.detail.revision'),
                    value: String(doc.revision),
                  },
                ]}
              />
              {identifiers.stub ? (
                <Banner variant="warning" live="none" title={t('fiscal.stub.title')}>
                  {t('fiscal.stub.body')}
                </Banner>
              ) : null}
              {doc.documentTypeIsPlaceholder ? (
                <Banner variant="info" live="none" title={t('fiscal.placeholder.title')}>
                  {t('fiscal.placeholder.body')}
                </Banner>
              ) : null}
              <Section title={t('fiscal.detail.identifiers')} family="brand">
                <dl className={styles.grid}>
                  <div>
                    <dt>{t('fiscal.columns.mark')}</dt>
                    <dd className="ds-mono">{dash(identifiers.mark)}</dd>
                  </div>
                  <div>
                    <dt>{t('fiscal.detail.uid')}</dt>
                    <dd className="ds-mono">{dash(identifiers.uid)}</dd>
                  </div>
                  <div>
                    <dt>{t('fiscal.detail.channel')}</dt>
                    <dd>{identifiers.channel}</dd>
                  </div>
                  <div>
                    <dt>{t('fiscal.detail.documentType')}</dt>
                    <dd className="ds-mono">{doc.documentType}</dd>
                  </div>
                  <div>
                    <dt>{t('fiscal.columns.source')}</dt>
                    <dd>
                      {doc.sourceType} <span className="ds-mono">{doc.sourceId}</span>
                    </dd>
                  </div>
                  <div>
                    <dt>{t('fiscal.detail.counterparty')}</dt>
                    <dd className="ds-mono">{doc.counterpartyPartyId}</dd>
                  </div>
                  {doc.correlatedDocumentId ? (
                    <div>
                      <dt>{t('fiscal.detail.corrects')}</dt>
                      <dd>
                        <LinkButton to={`/compliance/fiscal/${doc.correlatedDocumentId}`}>
                          {doc.correlatedDocumentId}
                        </LinkButton>
                      </dd>
                    </div>
                  ) : null}
                </dl>
              </Section>
              {doc.rejectionCodes?.length ? (
                <Section
                  title={t('fiscal.detail.rejections')}
                  family="danger"
                  count={doc.rejectionCodes.length}
                >
                  <div className={styles.codes}>
                    {doc.rejectionCodes.map((c) => (
                      <code key={c} className="ds-mono">
                        {c}
                      </code>
                    ))}
                  </div>
                </Section>
              ) : null}
              <Section
                title={t('fiscal.qr.title')}
                family="warning"
                actions={
                  showQr ? undefined : (
                    <Button
                      variant="secondary"
                      onPress={() => {
                        setShowQr(true);
                      }}
                    >
                      {t('fiscal.qr.load')}
                    </Button>
                  )
                }
              >
                {showQr ? (
                  <QueryView query={qr}>
                    {(payload) => (
                      <div className={styles.stackGap}>
                        <Banner
                          variant="warning"
                          live="none"
                          title={t('fiscal.qr.unverifiedTitle')}
                        >
                          {payload.layoutVerified
                            ? t('fiscal.qr.verified')
                            : t('fiscal.qr.unverified')}
                        </Banner>
                        <dl className={styles.grid}>
                          <div>
                            <dt>{t('fiscal.columns.mark')}</dt>
                            <dd className="ds-mono">{dash(payload.mark)}</dd>
                          </div>
                          <div>
                            <dt>{t('fiscal.detail.uid')}</dt>
                            <dd className="ds-mono">{dash(payload.uid)}</dd>
                          </div>
                          <div>
                            <dt>{t('fiscal.qr.ref')}</dt>
                            <dd className="ds-mono">{dash(payload.qrPayloadRef)}</dd>
                          </div>
                        </dl>
                        <pre
                          className={cx(styles.qr, 'ds-mono')}
                          aria-label={t('fiscal.qr.payload')}
                        >
                          {payload.qrPayload ?? t('fiscal.qr.none')}
                        </pre>
                      </div>
                    )}
                  </QueryView>
                ) : (
                  <p className="ds-caption">{t('fiscal.qr.body')}</p>
                )}
              </Section>
              <Section title={t('fiscal.detail.lines')} family="info" count={doc.lines.length}>
                <SimpleTable<Line>
                  aria-label={t('fiscal.detail.lines')}
                  columns={columns}
                  data={doc.lines}
                  getRowId={(l) => `${l.fiscalCategoryKey}-${l.chargeId ?? ''}-${l.amount.amount}`}
                  getRowLabel={(l) => l.fiscalCategoryKey}
                  emptyState={<p className="ds-caption">{t('fiscal.lines.empty')}</p>}
                />
              </Section>
              {action ? (
                <FiscalActionDialog
                  action={action}
                  data={data}
                  onClose={() => {
                    setAction(null);
                  }}
                />
              ) : null}
            </>
          );
        }}
      </QueryView>
    </div>
  );
}
