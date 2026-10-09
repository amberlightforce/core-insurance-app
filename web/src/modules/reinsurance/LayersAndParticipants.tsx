import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import {
  EmptyState,
  StatusPill,
  moneyColumn,
  percentColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { SimpleTable } from '../staff/SimpleTable';
import staff from '../staff/staff.module.css';
import type { ContractLayer, ContractParticipation, ContractView } from './api';

import { useLayerSummary } from './layerSummary';
import { microToPct, pctToMicro } from './money';
import { PartyName } from './PartyName';
import styles from './Reinsurance.module.css';
import { useRiFormat } from './useRiFormat';

/** SCR-RI-03 layers: attachment, limit, AAD, AAL per layer, with the «limit xs attachment» summary. */
export function LayersTable({ layers }: { layers: readonly ContractLayer[] }) {
  const { t } = useTranslation('reinsurance');
  const summaryOf = useLayerSummary();
  const rows = useMemo(() => [...layers].sort((a, b) => a.layerNo - b.layerNo), [layers]);
  const columns = useMemo<DataColumn<ContractLayer>[]>(
    () => [
      textColumn<ContractLayer>('layer', t('layers.columns.layer'), (l) => t('layers.layerNo', { no: l.layerNo }), { size: 110 }),
      textColumn<ContractLayer>('summary', t('layers.columns.summary'), (l) => summaryOf(l), { size: 190 }),
      moneyColumn<ContractLayer>('attachment', t('layers.columns.attachment'), (l) => l.attachment.amount, { size: 150 }),
      moneyColumn<ContractLayer>('limit', t('layers.columns.limit'), (l) => l.limit.amount, { size: 150 }),
      moneyColumn<ContractLayer>('aad', t('layers.columns.aad'), (l) => l.aad.amount, { size: 130 }),
      moneyColumn<ContractLayer>('aal', t('layers.columns.aal'), (l) => l.aal?.amount ?? null, { size: 150 }),
    ],
    [t, summaryOf],
  );
  return (
    <SimpleTable<ContractLayer>
      aria-label={t('layers.title')}
      columns={columns}
      data={rows}
      getRowId={(l) => String(l.layerNo)}
      emptyState={
        <EmptyState
          kind="first-use"
          headingLevel={3}
          illustration={<></>}
          headline={t('layers.emptyTitle')}
          description={t('layers.emptyBody')}
        />
      }
    />
  );
}


/** SCR-RI-04 participations: reinsurer, lead, broker, signed line, with the Σ signed lines vs placed % check. */
export function ParticipantsTable({ contract }: { contract: ContractView }) {
  const { t } = useTranslation('reinsurance');
  const fmt = useRiFormat();
  const rows = contract.participations;
  const columns = useMemo<DataColumn<ContractParticipation>[]>(
    () => [
      statusColumn<ContractParticipation>(
        'reinsurer',
        t('participants.columns.reinsurer'),
        (p) => p.reinsurerPartyId,
        (p) => <PartyName partyId={p.reinsurerPartyId} />,
        { size: 280, enableSorting: false },
      ),
      statusColumn<ContractParticipation>(
        'role',
        t('participants.columns.role'),
        (p) => (p.lead ? 'lead' : 'follower'),
        (p) =>
          p.lead ? (
            <StatusPill semantic="info" text={t('participants.lead')} announceChanges={false} />
          ) : (
            <span>{t('participants.follower')}</span>
          ),
        { size: 130 },
      ),
      statusColumn<ContractParticipation>(
        'broker',
        t('participants.columns.broker'),
        (p) => p.brokerPartyId ?? '',
        (p) => (p.brokerPartyId ? <PartyName partyId={p.brokerPartyId} /> : <span>{t('participants.direct')}</span>),
        { size: 220, enableSorting: false },
      ),
      percentColumn<ContractParticipation>('signedLine', t('participants.columns.signedLine'), (p) => p.signedLinePct, { size: 130 }),
    ],
    [t],
  );
  const signed = rows.reduce((sum, p) => sum + pctToMicro(p.signedLinePct), 0n);
  const placed = pctToMicro(contract.placedPct);
  const matches = signed === placed;
  const leads = rows.filter((p) => p.lead).length;
  return (
    <div className={staff.stack}>
      <SimpleTable<ContractParticipation>
        aria-label={t('participants.title')}
        columns={columns}
        data={rows}
        getRowId={(p) => p.reinsurerPartyId}
        emptyState={
          <EmptyState
            kind="first-use"
            headingLevel={3}
            illustration={<></>}
            headline={t('participants.emptyTitle')}
            description={t('participants.emptyBody')}
          />
        }
      />
      <p className={styles.note}>
        <StatusPill
          semantic={matches && leads === 1 ? 'success' : 'warning'}
          text={matches && leads === 1 ? t('participants.check.ok') : t('participants.check.bad')}
          announceChanges={false}
        />{' '}
        {t('participants.check.sum', {
          signed: fmt.percent(microToPct(signed)),
          placed: fmt.percent(contract.placedPct),
          leads,
        })}
      </p>
    </div>
  );
}
