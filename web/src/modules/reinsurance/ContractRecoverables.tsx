import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-aria-components';

import {
  Banner,
  EmptyState,
  LoadingState,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { cx } from '../../design-system/utils/cx';
import { ProblemBanner } from '../staff/ProblemBanner';
import { SimpleTable } from '../staff/SimpleTable';
import staff from '../staff/staff.module.css';
import { useContractRecoveries, type ContractView, type RecoveryRow } from './api';
import { fromMinor, toMinor } from './money';
import { PartyName } from './PartyName';
import { RecoveryStatePill } from './RecoveryStatePill';
import {
  money,
  recoverableTotals,
  type LayerYearTotal,
  type ParticipantYearTotal,
} from './recoverables';
import styles from './Reinsurance.module.css';
import { useRiFormat } from './useRiFormat';

interface ClaimLayerRow {
  id: string;
  claimId: string;
  layerNo: number;
  incurred: string;
  paid: string;
  state: RecoveryRow['state'];
}

/** One row per claim x layer, summed over the participants. Incurred and paid only: no per-claim outstanding (D-SL4-21). */
function claimRows(rows: readonly RecoveryRow[]): ClaimLayerRow[] {
  const map = new Map<string, { row: ClaimLayerRow; incurred: bigint; paid: bigint }>();
  for (const row of rows) {
    if (!row.claimId) continue;
    const layerNo = row.layerNo ?? 0;
    const id = `${row.claimId}|${String(layerNo)}`;
    const entry = map.get(id) ?? {
      row: {
        id,
        claimId: row.claimId,
        layerNo,
        incurred: '0.00',
        paid: '0.00',
        state: row.state ?? null,
      },
      incurred: 0n,
      paid: 0n,
    };
    entry.incurred += toMinor(row.recoverableIncurred.amount);
    entry.paid += toMinor(row.recoverablePaid.amount);
    map.set(id, entry);
  }
  return [...map.values()]
    .map(({ row, incurred, paid }) => ({
      ...row,
      incurred: fromMinor(incurred),
      paid: fromMinor(paid),
    }))
    .sort((a, b) => a.layerNo - b.layerNo || a.claimId.localeCompare(b.claimId));
}

/**
 * The recoverables of a treaty (REQ-RI-003, REQ-RI-136). Outstanding is a layer-year figure (D-SL4-21): the layer
 * table and the totals show it, the participant table shows the allocated share (D-SL4-22), and the per-claim
 * table has incurred and paid only.
 */
export function ContractRecoverables({ contract }: { contract: ContractView }) {
  const { t } = useTranslation('reinsurance');
  const fmt = useRiFormat();
  const { query, rows, partial } = useContractRecoveries(contract.contractId);
  const currency = contract.currency;
  const calculated = useMemo(() => {
    try {
      return {
        totals: recoverableTotals(rows, contract.participations),
        perClaim: claimRows(rows),
        invalid: false,
      };
    } catch {
      return {
        totals: { layers: [] as LayerYearTotal[], participants: [] as ParticipantYearTotal[] },
        perClaim: [] as ClaimLayerRow[],
        invalid: true,
      };
    }
  }, [rows, contract.participations]);
  const { totals, perClaim } = calculated;

  const layerColumns = useMemo<DataColumn<LayerYearTotal>[]>(
    () => [
      textColumn<LayerYearTotal>(
        'layer',
        t('recoverables.columns.layer'),
        (l) => t('layers.layerNo', { no: l.layerNo }),
        { size: 110 },
      ),
      moneyColumn<LayerYearTotal>(
        'incurred',
        t('recoverables.columns.incurred'),
        (l) => fromMinor(l.incurred),
        { size: 170, currency },
      ),
      moneyColumn<LayerYearTotal>(
        'paid',
        t('recoverables.columns.paid'),
        (l) => fromMinor(l.paid),
        { size: 170, currency },
      ),
      moneyColumn<LayerYearTotal>(
        'outstanding',
        t('recoverables.columns.layerOutstanding'),
        (l) => fromMinor(l.outstanding),
        { size: 190, currency },
      ),
    ],
    [t, currency],
  );
  const participantColumns = useMemo<DataColumn<ParticipantYearTotal>[]>(
    () => [
      textColumn<ParticipantYearTotal>(
        'layer',
        t('recoverables.columns.layer'),
        (p) => t('layers.layerNo', { no: p.layerNo }),
        { size: 110 },
      ),
      statusColumn<ParticipantYearTotal>(
        'reinsurer',
        t('recoverables.columns.reinsurer'),
        (p) => p.participantId,
        (p) => <PartyName partyId={p.participantId} />,
        { size: 240, enableSorting: false },
      ),
      moneyColumn<ParticipantYearTotal>(
        'incurred',
        t('recoverables.columns.incurred'),
        (p) => fromMinor(p.incurred),
        { size: 160, currency },
      ),
      moneyColumn<ParticipantYearTotal>(
        'paid',
        t('recoverables.columns.paid'),
        (p) => fromMinor(p.paid),
        { size: 160, currency },
      ),
      moneyColumn<ParticipantYearTotal>(
        'outstanding',
        t('recoverables.columns.allocatedOutstanding'),
        (p) => fromMinor(p.outstanding),
        { size: 190, currency },
      ),
    ],
    [t, currency],
  );
  const claimColumns = useMemo<DataColumn<ClaimLayerRow>[]>(
    () => [
      statusColumn<ClaimLayerRow>(
        'claim',
        t('recoverables.columns.claim'),
        (r) => r.claimId,
        (r) => (
          <Link href={`/reinsurance/claims/${r.claimId}`} className={cx(styles.wrapId)}>
            {r.claimId}
          </Link>
        ),
        { size: 330 },
      ),
      textColumn<ClaimLayerRow>(
        'layer',
        t('recoverables.columns.layer'),
        (r) => t('layers.layerNo', { no: r.layerNo }),
        { size: 110 },
      ),
      moneyColumn<ClaimLayerRow>(
        'incurred',
        t('recoverables.columns.incurred'),
        (r) => r.incurred,
        { size: 160, currency },
      ),
      moneyColumn<ClaimLayerRow>('paid', t('recoverables.columns.paid'), (r) => r.paid, {
        size: 160,
        currency,
      }),
      statusColumn<ClaimLayerRow>(
        'state',
        t('recoverables.columns.state'),
        (r) => r.state ?? '',
        (r) => <RecoveryStatePill state={r.state ?? null} />,
        { size: 150 },
      ),
    ],
    [t, currency],
  );

  if (query.isPending) return <LoadingState immediate>{t('recoverables.loading')}</LoadingState>;
  if (query.isError) {
    return (
      <ProblemBanner
        error={query.error}
        title={t('recoverables.failed')}
        onRetry={() => {
          void query.refetch();
        }}
      />
    );
  }
  if (calculated.invalid) return <Banner variant="danger" title={t('recoverables.invalid')} />;
  if (rows.length === 0) {
    return (
      <EmptyState
        kind="first-use"
        headingLevel={3}
        illustration={<></>}
        headline={t('recoverables.emptyTitle')}
        description={t('recoverables.emptyBody')}
      />
    );
  }

  const incurred = totals.layers.reduce((sum, l) => sum + l.incurred, 0n);
  const paid = totals.layers.reduce((sum, l) => sum + l.paid, 0n);
  const outstanding = totals.layers.reduce((sum, l) => sum + l.outstanding, 0n);

  return (
    <div className={staff.stack}>
      {partial ? (
        <Banner variant="warning" live="none" title={t('recoverables.partialTitle')}>
          {t('recoverables.partialBody')}
        </Banner>
      ) : null}
      <dl className={styles.tiles}>
        <div className={styles.tile}>
          <dt className={styles.tileLabel}>{t('recoverables.totals.incurred')}</dt>
          <dd className={styles.tileValue}>{fmt.money(money(incurred, currency))}</dd>
        </div>
        <div className={styles.tile}>
          <dt className={styles.tileLabel}>{t('recoverables.totals.paid')}</dt>
          <dd className={styles.tileValue}>{fmt.money(money(paid, currency))}</dd>
        </div>
        <div className={styles.tile}>
          <dt className={styles.tileLabel}>{t('recoverables.totals.outstanding')}</dt>
          <dd className={styles.tileValue}>{fmt.money(money(outstanding, currency))}</dd>
        </div>
      </dl>
      <p className={styles.note}>{t('recoverables.outstandingNote')}</p>
      <h3 className="ds-heading-3">{t('recoverables.byLayer')}</h3>
      <SimpleTable<LayerYearTotal>
        aria-label={t('recoverables.byLayer')}
        columns={layerColumns}
        data={totals.layers}
        getRowId={(l) => String(l.layerNo)}
      />
      <h3 className="ds-heading-3">{t('recoverables.byParticipant')}</h3>
      <SimpleTable<ParticipantYearTotal>
        aria-label={t('recoverables.byParticipant')}
        columns={participantColumns}
        data={totals.participants}
        getRowId={(p) => `${String(p.layerNo)}|${p.participantId}`}
      />
      <p className={styles.note}>{t('recoverables.allocatedNote')}</p>
      <h3 className="ds-heading-3">{t('recoverables.byClaim')}</h3>
      <SimpleTable<ClaimLayerRow>
        aria-label={t('recoverables.byClaim')}
        columns={claimColumns}
        data={perClaim}
        getRowId={(r) => r.id}
      />
      <p className={styles.note}>{t('recoverables.perClaimNote')}</p>
    </div>
  );
}
