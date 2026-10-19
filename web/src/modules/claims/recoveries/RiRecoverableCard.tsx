import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { Button, Drawer, moneyColumn, textColumn, type DataColumn } from '../../../design-system';
import { isApiError } from '../../../api/client';
import { useClaimRecoveries, type ClaimRecoveryRow } from '../../reinsurance/api';
import { Section } from '../../staff/PageHeader';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useRecoveryTrace } from './api';

function TraceDrawer({ batchId, onClose }: { batchId: string | null; onClose: () => void }) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const trace = useRecoveryTrace(batchId);
  return (
    <Drawer
      isOpen={batchId !== null}
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      title={t('riRec.trace.title')}
      width={400}
    >
      {trace.isPending ? (
        <p className={styles.muted}>{t('riRec.trace.loading')}</p>
      ) : trace.isError ? (
        <p className={styles.muted}>{t('riRec.trace.failed')}</p>
      ) : (
        <div className={styles.stack}>
          <p className={styles.muted}>
            {t('riRec.trace.engine', { version: trace.data.engineVersion ?? '—' })}
          </p>
          <ol className={styles.stack} aria-label={t('riRec.trace.title')}>
            {trace.data.trace.map((step) => (
              <li key={step.stepNo}>
                <strong>{t(`riRec.trace.kind.${step.kind}`, { defaultValue: step.kind })}</strong>
                {step.amount ? ` · ${fmt.money(step.amount)}` : ''}
                {step.note ? <span className="ds-caption"> {step.note}</span> : null}
              </li>
            ))}
          </ol>
        </div>
      )}
    </Drawer>
  );
}

/**
 * «Αντασφαλιστική ανάκτηση» (REQ-CLM-229, F-407): read-only. Per treaty and layer: incurred, paid and outstanding
 * recoverable from ri.Recovery.listByClaim, with a link to the calculation trace. Hidden when RI has no rows.
 */
export function RiRecoverableCard({ claimId }: { claimId: string }) {
  const { t } = useTranslation('claims');
  const { query, rows } = useClaimRecoveries(claimId);
  const [batch, setBatch] = useState<string | null>(null);

  const columns = useMemo<DataColumn<ClaimRecoveryRow>[]>(
    () => [
      textColumn<ClaimRecoveryRow>('contract', t('riRec.columns.contract'), (r) =>
        t('riRec.contract', { year: r.contractYear ?? '—', layer: r.layerNo ?? '—' }),
      ),
      moneyColumn<ClaimRecoveryRow>(
        'incurred',
        t('riRec.columns.incurred'),
        (r) => r.recoverableIncurred.amount,
        { currency: 'EUR' },
      ),
      moneyColumn<ClaimRecoveryRow>(
        'paid',
        t('riRec.columns.paid'),
        (r) => r.recoverablePaid.amount,
        { currency: 'EUR' },
      ),
      moneyColumn<ClaimRecoveryRow>(
        'outstanding',
        t('riRec.columns.outstanding'),
        (r) => r.recoverableOutstanding.amount,
        { currency: 'EUR' },
      ),
    ],
    [t],
  );

  if (query.isPending) return null;
  if (query.isError) {
    // RI answers 404 when no contract covers the claim: say so plainly; any other failure hides the card.
    return isApiError(query.error) && query.error.status === 404 ? (
      <Section title={t('riRec.title')}>
        <p className={styles.muted}>{t('riRec.noContract')}</p>
      </Section>
    ) : null;
  }
  if (rows.length === 0) return null;
  const traceable = rows.find((r) => r.batchId);
  return (
    <Section title={t('riRec.title')}>
      <div className={styles.stack}>
        <SimpleTable<ClaimRecoveryRow>
          aria-label={t('riRec.title')}
          columns={columns}
          data={rows}
          getRowId={(r) =>
            r.recoveryId ?? `${r.contractId}:${r.layerId ?? ''}:${r.participantId ?? ''}`
          }
        />
        {traceable?.batchId ? (
          <Button
            variant="secondary"
            onPress={() => {
              setBatch(traceable.batchId ?? null);
            }}
          >
            {t('riRec.trace.open')}
          </Button>
        ) : null}
        <TraceDrawer
          batchId={batch}
          onClose={() => {
            setBatch(null);
          }}
        />
      </div>
    </Section>
  );
}
