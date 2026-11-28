import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { Banner, Button, toast } from '../../../design-system';
import { useDevSession } from '../../../dev-auth/devAuth';
import { downloadArchive } from '../../documents/api';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import staff from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { sameActor, useSubmission, type SubmissionView } from './api';
import { SubmissionActionDialog, type SubmissionAction } from './SubmissionActionDialog';
import styles from './Submissions.module.css';
import { ApprovalPill, OverduePill, SubmissionStatusPill } from './StatusPills';

const dash = (v?: string | null) => v ?? '—';

/** One submission: lifecycle actions, evidence, filing and response facts, and the history timeline. */
export function SubmissionPage() {
  const { t } = useTranslation('compliance');
  const { id = '' } = useParams();
  const query = useSubmission(id);
  const [action, setAction] = useState<SubmissionAction | null>(null);

  return (
    <div className={staff.page}>
      <QueryView query={query} notFoundMessage={t('submissions.notFound')}>
        {(s) => (
          <Detail
            s={s}
            action={action}
            setAction={setAction}
            actions={<Actions s={s} setAction={setAction} />}
          />
        )}
      </QueryView>
    </div>
  );
}

/** Which lifecycle buttons the state allows; the server enforces the same rules and the maker-checker. */
function Actions({
  s,
  setAction,
}: {
  s: SubmissionView;
  setAction: (a: SubmissionAction) => void;
}) {
  const { t } = useTranslation('compliance');
  const session = useDevSession();
  const actorKey = session?.user.actorKey ?? '';
  const four = sameActor(actorKey, s.preparedBy) || sameActor(actorKey, s.requestedBy);
  const btn = (a: SubmissionAction, variant: 'primary' | 'secondary' = 'secondary') => (
    <Button
      key={a}
      variant={variant}
      onPress={() => {
        setAction(a);
      }}
    >
      {t(`submissions.actions.${a}.button`)}
    </Button>
  );
  const editable = s.status === 'PLANNED' || s.status === 'IN_PREPARATION';
  return (
    <div className={styles.actions}>
      {editable ? btn('attach') : null}
      {editable ? btn('pack') : null}
      {s.status === 'IN_PREPARATION' ? btn('request', 'primary') : null}
      {s.status === 'READY' && s.approvalStatus === 'PENDING' && !four ? (
        <>
          {btn('approve', 'primary')}
          {btn('reject')}
        </>
      ) : null}
      {s.status === 'READY' && s.approvalStatus === 'PENDING' && four ? (
        <p className="ds-caption">{t('submissions.fourEyes')}</p>
      ) : null}
      {s.status === 'READY' && s.approvalStatus === 'APPROVED' ? btn('filing', 'primary') : null}
      {s.status === 'SUBMITTED' ? btn('response', 'primary') : null}
      {s.status === 'REJECTED' ? btn('resubmit', 'primary') : null}
      {btn('comment')}
    </div>
  );
}

function Detail({
  s,
  action,
  setAction,
  actions,
}: {
  s: SubmissionView;
  action: SubmissionAction | null;
  setAction: (a: SubmissionAction | null) => void;
  actions: React.ReactNode;
}) {
  const { t } = useTranslation('compliance');
  const fmt = useFormat();
  const [downloadError, setDownloadError] = useState<unknown>(null);

  const download = async (archiveId: string) => {
    setDownloadError(null);
    try {
      await downloadArchive(archiveId, `${s.number}-${archiveId}.zip`);
      toast.success({ title: t('submissions.evidence.downloaded') });
    } catch (error) {
      setDownloadError(error);
    }
  };

  return (
    <>
      <PageHeader
        overline={t('submissions.overline')}
        title={s.submissionType}
        recordId={s.number}
        subtitle={
          <>
            <SubmissionStatusPill status={s.status} />
            {s.overdue ? <OverduePill /> : null}
          </>
        }
        actions={
          <LinkButton variant="secondary" to="/compliance/submissions">
            {t('submissions.back')}
          </LinkButton>
        }
        facts={[
          { id: 'regulator', label: t('submissions.columns.regulator'), value: s.regulator },
          { id: 'period', label: t('submissions.columns.period'), value: s.periodLabel },
          {
            id: 'due',
            label: t('submissions.columns.due'),
            value: s.dueDate ? fmt.date(s.dueDate) : '—',
          },
          { id: 'version', label: t('submissions.version'), value: String(s.version) },
        ]}
      />
      {s.overdue ? (
        <Banner variant="danger" live="none" title={t('submissions.overdueTitle')}>
          {t('submissions.overdueBody')}
        </Banner>
      ) : null}
      {s.dueDate && s.dueLegalStatus === 'Unverified' ? (
        <Banner variant="warning" live="none" title={t('submissions.unverifiedTitle')}>
          {t('submissions.unverifiedBody')}
        </Banner>
      ) : null}
      <Section
        title={t('submissions.lifecycle')}
        family="brand"
        meta={<ApprovalPill status={s.approvalStatus} />}
      >
        <div className={styles.stack}>
          <dl className={styles.grid}>
            <div>
              <dt>{t('submissions.preparedBy')}</dt>
              <dd>{dash(s.preparedBy)}</dd>
            </div>
            <div>
              <dt>{t('submissions.requestedBy')}</dt>
              <dd>{dash(s.requestedBy)}</dd>
            </div>
            <div>
              <dt>{t('submissions.approvedBy')}</dt>
              <dd>{dash(s.approvedBy)}</dd>
            </div>
            {s.previousVersionId ? (
              <div>
                <dt>{t('submissions.resubmits')}</dt>
                <dd>
                  <LinkButton to={`/compliance/submissions/${s.previousVersionId}`}>
                    {s.previousVersionId}
                  </LinkButton>
                </dd>
              </div>
            ) : null}
            {s.note ? (
              <div>
                <dt>{t('submissions.note')}</dt>
                <dd>{s.note}</dd>
              </div>
            ) : null}
          </dl>
          {actions}
        </div>
      </Section>
      <Section title={t('submissions.evidence.title')} family="info" count={s.evidence.length}>
        {downloadError ? (
          <ProblemBanner error={downloadError} title={t('submissions.evidence.downloadFailed')} />
        ) : null}
        {s.evidence.length === 0 ? (
          <p className="ds-caption">{t('submissions.evidence.empty')}</p>
        ) : (
          <ul className={styles.list} aria-label={t('submissions.evidence.title')}>
            {s.evidence.map((e) => (
              <li key={`${e.kind}-${e.refId}`} className={styles.evidence}>
                <div className={styles.evidenceMain}>
                  <span>
                    {t(`submissions.kind.${e.kind}`, { defaultValue: e.kind })}
                    {e.label ? ` · ${e.label}` : ''}
                  </span>
                  <span className="ds-mono">{e.refId}</span>
                  {e.hash ? <span className="ds-mono ds-caption">{e.hash}</span> : null}
                  <span className="ds-caption">
                    {[e.addedBy, e.addedAt ? fmt.dateTime(e.addedAt) : null]
                      .filter(Boolean)
                      .join(' · ')}
                  </span>
                </div>
                {e.kind === 'EVIDENCE_PACK' ? (
                  <Button
                    variant="secondary"
                    onPress={() => {
                      void download(e.refId);
                    }}
                  >
                    {t('submissions.evidence.download')}
                  </Button>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </Section>
      <Section title={t('submissions.filing.title')} family="success">
        {s.filingReference || s.responseOutcome ? (
          <dl className={styles.grid}>
            <div>
              <dt>{t('submissions.filing.channel')}</dt>
              <dd>{dash(s.filingChannel)}</dd>
            </div>
            <div>
              <dt>{t('submissions.filing.reference')}</dt>
              <dd className="ds-mono">{dash(s.filingReference)}</dd>
            </div>
            <div>
              <dt>{t('submissions.filing.filedAt')}</dt>
              <dd>{s.filedAt ? fmt.dateTime(s.filedAt) : '—'}</dd>
            </div>
            <div>
              <dt>{t('submissions.filing.filer')}</dt>
              <dd>{dash(s.filer)}</dd>
            </div>
            <div>
              <dt>{t('submissions.filing.receiptHash')}</dt>
              <dd className="ds-mono">{dash(s.receiptHash)}</dd>
            </div>
            <div>
              <dt>{t('submissions.response.outcome')}</dt>
              <dd>
                {s.responseOutcome
                  ? t(`submissions.status.${s.responseOutcome}`, {
                      defaultValue: s.responseOutcome,
                    })
                  : '—'}
              </dd>
            </div>
            <div>
              <dt>{t('submissions.response.reference')}</dt>
              <dd className="ds-mono">{dash(s.responseReference)}</dd>
            </div>
            <div>
              <dt>{t('submissions.response.respondedAt')}</dt>
              <dd>{s.respondedAt ? fmt.dateTime(s.respondedAt) : '—'}</dd>
            </div>
            {s.responseNote ? (
              <div>
                <dt>{t('submissions.response.note')}</dt>
                <dd>{s.responseNote}</dd>
              </div>
            ) : null}
          </dl>
        ) : (
          <p className="ds-caption">{t('submissions.filing.empty')}</p>
        )}
      </Section>
      <Section title={t('submissions.history.title')} family="neutral" count={s.history.length}>
        <ol className={styles.history} aria-label={t('submissions.history.title')}>
          {s.history.map((h, i) => (
            <li key={`${h.at}-${String(i)}`} className={styles.event}>
              <span className={styles.dot} data-kind={h.kind} aria-hidden="true" />
              <div className={styles.eventBody}>
                <span>
                  {h.kind === 'TRANSITION'
                    ? t('submissions.history.transition', {
                        from: h.fromStatus
                          ? t(`submissions.status.${h.fromStatus}`, { defaultValue: h.fromStatus })
                          : '—',
                        to: h.toStatus
                          ? t(`submissions.status.${h.toStatus}`, { defaultValue: h.toStatus })
                          : '—',
                      })
                    : t('submissions.history.comment')}
                </span>
                {h.text ? <span>{h.text}</span> : null}
                <span className={styles.eventMeta}>
                  {h.actor} · {fmt.dateTime(h.at)}
                </span>
              </div>
            </li>
          ))}
        </ol>
      </Section>
      {action ? (
        <SubmissionActionDialog
          action={action}
          submission={s}
          onClose={() => {
            setAction(null);
          }}
        />
      ) : null}
    </>
  );
}
