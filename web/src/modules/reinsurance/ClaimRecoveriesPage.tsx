import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-aria-components';
import { useParams } from 'react-router';

import {
  EmptyState,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { cx } from '../../design-system/utils/cx';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import staff from '../staff/staff.module.css';
import { useClaimRecoveries, type ClaimRecoveryRow } from './api';
import { fromMinor, toMinor } from './money';
import { PartyName } from './PartyName';
import { RecoveryStatePill } from './RecoveryStatePill';
import styles from './Reinsurance.module.css';
import { useRiFormat } from './useRiFormat';

/**
 * The recoverables of one claim (REQ-RI-003, REQ-RI-136): per treaty x layer x reinsurer, the incurred and the paid
 * recoverable. There is deliberately no outstanding here: outstanding is a layer-year figure on the treaty (D-SL4-21).
 */
export function ClaimRecoveriesPage() {
  const { t } = useTranslation('reinsurance');
  const fmt = useRiFormat();
  const { claimId = '' } = useParams();
  const { query, rows } = useClaimRecoveries(claimId);

  const columns = useMemo<DataColumn<ClaimRecoveryRow>[]>(
    () => [
      statusColumn<ClaimRecoveryRow>(
        'contract',
        t('claim.columns.contract'),
        (r) => String(r.contractYear ?? ''),
        (r) => (
          <Link href={`/reinsurance/contracts/${r.contractId}`} className={cx(styles.wrapId)}>
            {t('claim.contractLink', { year: r.contractYear ?? '—' })}
          </Link>
        ),
        { size: 170 },
      ),
      textColumn<ClaimRecoveryRow>('layer', t('claim.columns.layer'), (r) => (r.layerNo ? t('layers.layerNo', { no: r.layerNo }) : '—'), { size: 110 }),
      statusColumn<ClaimRecoveryRow>(
        'reinsurer',
        t('claim.columns.reinsurer'),
        (r) => r.participantId,
        (r) => <PartyName partyId={r.participantId} />,
        { size: 240, enableSorting: false },
      ),
      moneyColumn<ClaimRecoveryRow>('incurred', t('claim.columns.incurred'), (r) => r.recoverableIncurred.amount, { size: 170, currency: 'EUR' }),
      moneyColumn<ClaimRecoveryRow>('paid', t('claim.columns.paid'), (r) => r.recoverablePaid.amount, { size: 170, currency: 'EUR' }),
      statusColumn<ClaimRecoveryRow>(
        'state',
        t('claim.columns.state'),
        (r) => r.state ?? '',
        (r) => <RecoveryStatePill state={r.state ?? null} />,
        { size: 150 },
      ),
    ],
    [t],
  );

  const totals = useMemo(
    () =>
      rows.reduce(
        (sum, r) => ({
          incurred: sum.incurred + toMinor(r.recoverableIncurred.amount),
          paid: sum.paid + toMinor(r.recoverablePaid.amount),
        }),
        { incurred: 0n, paid: 0n },
      ),
    [rows],
  );
  const currency = rows[0]?.recoverableIncurred.currency ?? 'EUR';

  return (
    <div className={staff.page}>
      <PageHeader
        overline={t('overline')}
        title={t('claim.title')}
        recordId={claimId}
        actions={
          <>
            <LinkButton variant="secondary" to={`/claims/${claimId}`}>
              {t('claim.openClaim')}
            </LinkButton>
            <LinkButton variant="secondary" to="/reinsurance">
              {t('contract.back')}
            </LinkButton>
          </>
        }
      />
      <QueryView query={query} notFoundMessage={t('claim.notFound')}>
        {() => (
          <Section title={t('claim.section')} family="success" meta={t('claim.meta')}>
            {rows.length > 0 ? (
              <dl className={styles.tiles}>
                <div className={styles.tile}>
                  <dt className={styles.tileLabel}>{t('claim.totals.incurred')}</dt>
                  <dd className={styles.tileValue}>
                    {fmt.money({ amount: fromMinor(totals.incurred), currency })}
                  </dd>
                </div>
                <div className={styles.tile}>
                  <dt className={styles.tileLabel}>{t('claim.totals.paid')}</dt>
                  <dd className={styles.tileValue}>
                    {fmt.money({ amount: fromMinor(totals.paid), currency })}
                  </dd>
                </div>
              </dl>
            ) : null}
            <SimpleTable<ClaimRecoveryRow>
              aria-label={t('claim.section')}
              columns={columns}
              data={rows}
              getRowId={(r) => r.recoveryId ?? `${r.contractId}|${r.layerId}|${r.participantId}`}
              emptyState={
                <EmptyState
                  kind="first-use"
                  headingLevel={3}
                  illustration={<></>}
                  headline={t('claim.emptyTitle')}
                  description={t('claim.emptyBody')}
                />
              }
            />
            <p className={styles.note}>{t('claim.outstandingNote')}</p>
          </Section>
        )}
      </QueryView>
    </div>
  );
}
