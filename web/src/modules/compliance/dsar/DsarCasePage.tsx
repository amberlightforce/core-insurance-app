import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { Banner, Button } from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import staff from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { CaseActions } from './CaseActions';
import { downloadPackage } from './download';
import { DisclosurePanel } from './DisclosurePanel';
import { notBuiltModules, useDsar, type DsarOutcomeView } from './api';
import styles from './Dsar.module.css';
import { CaseStatusPill } from './StatusPills';
import { OutcomesTable, TasksPanel } from './TasksPanel';

/** One DSAR case: identity, fan-out per module, the DPO's decisions and approval, export and the disclosure log. */
export function DsarCasePage() {
  const { t } = useTranslation('compliance');
  const fmt = useFormat();
  const { dsarId = '' } = useParams();
  const query = useDsar(dsarId);

  return (
    <div className={staff.page}>
      <QueryView query={query} notFoundMessage={t('dsar.notFound')}>
        {(data) => {
          const { request } = data;
          const tasks = data.tasks ?? [];
          const blockers = request.blockers ?? [];
          const notBuilt = notBuiltModules(request);
          const outcomes: DsarOutcomeView[] = tasks.flatMap((task) => task.outcomes ?? []);
          return (
            <>
              <PageHeader
                overline={t('dsar.overline')}
                title={t(`dsar.type.${request.requestType}`)}
                recordId={request.dsarId}
                subtitle={<CaseStatusPill status={request.status} />}
                actions={
                  <LinkButton variant="secondary" to="/compliance/dsar">
                    {t('dsar.back')}
                  </LinkButton>
                }
                facts={[
                  {
                    id: 'subject',
                    label: t('dsar.facts.subject'),
                    value: <span className="ds-mono">{request.subjectPartyId}</span>,
                  },
                  {
                    id: 'deadline',
                    label: t('dsar.facts.deadline'),
                    value: fmt.date(request.deadline),
                  },
                  { id: 'creator', label: t('dsar.facts.creator'), value: request.createdBy },
                  {
                    id: 'identity',
                    label: t('dsar.facts.identity'),
                    value: t(request.identityVerified ? 'dsar.yes' : 'dsar.no'),
                  },
                  {
                    id: 'hold',
                    label: t('dsar.facts.legalHold'),
                    value: t(request.legalHold ? 'dsar.yes' : 'dsar.no'),
                  },
                  {
                    id: 'approval',
                    label: t('dsar.facts.approval'),
                    value: request.approvedBy
                      ? `${request.approvalAction ?? ''} · ${request.approvedBy}`
                      : '—',
                  },
                ]}
              />
              {request.refusalReason ? (
                <Banner variant="warning" live="none" title={t('dsar.refused')}>
                  {request.refusalReason}
                </Banner>
              ) : null}
              {notBuilt.length > 0 ? (
                <Banner variant="warning" live="none" title={t('dsar.notBuilt.title')}>
                  {t('dsar.notBuilt.body', { modules: notBuilt.join(', ') })}
                </Banner>
              ) : null}
              {blockers.length > 0 ? (
                <Section title={t('dsar.blockers.title')} count={blockers.length}>
                  <ul className={styles.blockers} aria-label={t('dsar.blockers.title')}>
                    {blockers.map((b) => (
                      <li key={b} className="ds-mono">
                        {b}
                      </li>
                    ))}
                  </ul>
                </Section>
              ) : null}
              <Section title={t('dsar.decision')}>
                <CaseActions data={data} />
              </Section>
              <Section title={t('dsar.tasks.title')} count={tasks.length}>
                <TasksPanel request={request} tasks={tasks} />
              </Section>
              <Section
                title={t('dsar.outcomes.title')}
                count={outcomes.length}
                actions={
                  data.package ? (
                    <Button
                      variant="secondary"
                      onPress={() => {
                        downloadPackage(request.dsarId, data.package);
                      }}
                    >
                      {t('dsar.actions.download')}
                    </Button>
                  ) : undefined
                }
              >
                <OutcomesTable outcomes={outcomes} />
              </Section>
              <Section title={t('dsar.disclosure.title')}>
                <DisclosurePanel dsarId={request.dsarId} partyId={request.subjectPartyId} />
              </Section>
            </>
          );
        }}
      </QueryView>
    </div>
  );
}
