import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { KeyValueList } from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import staff from '../staff/staff.module.css';
import { useContract, type ContractView } from './api';
import { ContractRecoverables } from './ContractRecoverables';
import { ContractStatusPill } from './ContractStatusPill';
import { DecisionBar } from './DecisionBar';
import { LayerDiagram } from './LayerDiagram';
import { LayersTable, ParticipantsTable } from './LayersAndParticipants';
import styles from './Reinsurance.module.css';
import { StageStrip } from './StageStrip';
import { useRiFormat } from './useRiFormat';
import { accountantRole, currentUser, hasRole } from './roles';

function periodText(
  contract: ContractView,
  date: (value: string | null | undefined) => string,
  open: string,
) {
  return `${date(contract.period.from)} – ${contract.period.to ? date(contract.period.to) : open}`;
}

function ContractDetails({ contract }: { contract: ContractView }) {
  const { t, i18n } = useTranslation('reinsurance');
  const fmt = useRiFormat();
  const typeLabel = t(`type.${contract.contractType}`);
  const codeLabel = (kind: 'product' | 'coverage', code: string) =>
    i18n.exists(`reinsurance:codes.${kind}.${code}`) ? t(`codes.${kind}.${code}`) : code;
  const yes = t('terms.yes');
  const no = t('terms.no');

  return (
    <div className={staff.stack}>
      <PageHeader
        overline={`${t('overline')} · ${t('contract.kind')}`}
        title={`${typeLabel} · ${String(contract.contractYear)}`}
        {...(contract.contractNumber ? { recordId: contract.contractNumber } : {})}
        subtitle={<ContractStatusPill status={contract.status} />}
        facts={[
          {
            id: 'period',
            label: t('contract.facts.period'),
            value: periodText(contract, fmt.date, t('contract.openEnded')),
          },
          { id: 'currency', label: t('contract.facts.currency'), value: contract.currency },
          {
            id: 'placed',
            label: t('contract.facts.placed'),
            value: fmt.percent(contract.placedPct),
          },
          { id: 'entity', label: t('contract.facts.entity'), value: contract.legalEntity },
          ...(contract.stableTreatyId
            ? [{ id: 'treaty', label: t('contract.facts.treaty'), value: contract.stableTreatyId }]
            : []),
        ]}
        actions={
          <>
            {contract.status === 'DRAFT' &&
            (hasRole(currentUser().roles, accountantRole) ||
              hasRole(currentUser().roles, 'Platform.Admin')) ? (
              <LinkButton to={`/reinsurance/contracts/${contract.contractId}/edit`}>
                {t('contract.edit')}
              </LinkButton>
            ) : null}
            <LinkButton variant="secondary" to="/reinsurance">
              {t('contract.back')}
            </LinkButton>
          </>
        }
      >
        <StageStrip status={contract.status} />
      </PageHeader>
      <div className={styles.body}>
        <div className={staff.stack}>
          <Section title={t('layers.title')} meta={t('layers.meta')} count={contract.layers.length}>
            <LayersTable layers={contract.layers} />
          </Section>
          <Section
            title={t('participants.title')}
            meta={t('participants.meta')}
            count={contract.participations.length}
          >
            <ParticipantsTable contract={contract} />
          </Section>
          <Section title={t('recoverables.title')} family="success" meta={t('recoverables.meta')}>
            <ContractRecoverables contract={contract} />
          </Section>
        </div>
        <div className={staff.stack}>
          <Section title={t('decision.title')} family="plum">
            <DecisionBar contract={contract} />
          </Section>
          <Section title={t('layers.diagram')} family="info">
            <LayerDiagram layers={contract.layers} />
          </Section>
          <Section title={t('terms.title')}>
            <KeyValueList
              aria-label={t('terms.title')}
              items={[
                {
                  id: 'products',
                  label: t('terms.products'),
                  value: contract.scope.productCodes.map((c) => codeLabel('product', c)).join(', '),
                },
                {
                  id: 'coverages',
                  label: t('terms.coverages'),
                  value: contract.scope.coverageCodes
                    .map((c) => codeLabel('coverage', c))
                    .join(', '),
                },
                {
                  id: 'alae',
                  label: t('terms.alae'),
                  value: contract.clause.alaeIncluded ? yes : no,
                },
                {
                  id: 'interest',
                  label: t('terms.interest'),
                  value: contract.clause.statutoryInterestIncluded ? yes : no,
                },
                {
                  id: 'inure',
                  label: t('terms.inure'),
                  value: t(`terms.inureValue.${contract.clause.recoveriesInure}`),
                },
              ]}
            />
          </Section>
          <Section title={t('history.title')}>
            <KeyValueList
              aria-label={t('history.title')}
              items={[
                {
                  id: 'maker',
                  label: t('history.maker'),
                  value: contract.maker ?? null,
                  kind: 'mono',
                },
                {
                  id: 'created',
                  label: t('history.created'),
                  value: fmt.dateTime(contract.createdAt),
                },
                {
                  id: 'approval',
                  label: t('history.approval'),
                  value: contract.approvalRequestId ?? null,
                  kind: 'mono',
                },
                {
                  id: 'activated',
                  label: t('history.activated'),
                  value: contract.activatedAt ? fmt.dateTime(contract.activatedAt) : null,
                },
                { id: 'version', label: t('history.version'), value: String(contract.versionNo) },
              ]}
            />
          </Section>
        </div>
      </div>
    </div>
  );
}

/** SCR-RI-02/03/04 subset: one treaty with stages, layers, participations, recoverables and the maker-checker bar. */
export function ContractPage() {
  const { t } = useTranslation('reinsurance');
  const { contractId = '' } = useParams();
  const query = useContract(contractId);
  return (
    <div className={staff.page}>
      <QueryView query={query} notFoundMessage={t('contract.notFound')}>
        {(contract) => <ContractDetails contract={contract} />}
      </QueryView>
    </div>
  );
}
