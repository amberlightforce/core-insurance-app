import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import { Checkbox, Dialog, TextField, announce } from '../../../design-system';
import { ProblemBanner } from '../../staff/ProblemBanner';
import {
  BULK_LIMIT,
  documentNumber,
  useFiscalCommand,
  type BulkResult,
  type FiscalDocument,
} from './api';
import styles from './Fiscal.module.css';
import { FiscalStatusPill } from './StatusPills';

/**
 * Bulk resubmit of rejected documents (max 50). The server answers per item (RESUBMITTED or SKIPPED with a code);
 * the per-item results replace the selection so the operator sees exactly what happened.
 */
export function BulkResubmitDialog({
  rows,
  onClose,
}: {
  rows: FiscalDocument[];
  onClose: () => void;
}) {
  const { t } = useTranslation('compliance');
  const { keyFor, release } = useIdempotencyKey();
  const run = useFiscalCommand();
  const rejected = useMemo(() => rows.filter((r) => r.document.status === 'REJECTED'), [rows]);
  const [selected, setSelected] = useState<string[]>([]);
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  const [result, setResult] = useState<BulkResult | null>(null);
  const byId = useMemo(
    () => new Map(rows.map((r) => [r.document.fiscalDocumentId, r] as const)),
    [rows],
  );

  const reasonValid = reason.trim().length > 0 && reason.trim().length <= 500;
  const countValid = selected.length >= 1 && selected.length <= BULK_LIMIT;

  const submit = async () => {
    setTried(true);
    if (!reasonValid || !countValid) return;
    const command = {
      name: 'bulk-resubmit' as const,
      body: { ids: selected, reason: reason.trim() },
    };
    const response = (await run.mutateAsync({ command, key: keyFor(command) })) as BulkResult;
    release();
    setResult(response);
    announce(
      t('fiscal.bulk.summary', {
        requested: response.requested,
        resubmitted: response.resubmitted,
        skipped: response.skipped,
      }),
    );
  };

  return (
    <Dialog
      isOpen
      title={t('fiscal.bulk.title')}
      size="lg"
      closeOnAction={false}
      isBusy={run.isPending}
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      onCancel={onClose}
      cancelLabel={result ? t('fiscal.bulk.close') : t('fiscal.actions.cancelButton')}
      {...(result
        ? {}
        : {
            primaryAction: {
              label: t('fiscal.bulk.submit', { count: selected.length }),
              variant: 'primary' as const,
              onAction: submit,
            },
          })}
    >
      <div className={styles.form}>
        {result ? (
          <>
            <p role="status">
              {t('fiscal.bulk.summary', {
                requested: result.requested,
                resubmitted: result.resubmitted,
                skipped: result.skipped,
              })}
            </p>
            <ul className={styles.results} aria-label={t('fiscal.bulk.results')}>
              {result.results.map((item) => {
                const row = byId.get(item.fiscalDocumentId);
                return (
                  <li key={item.fiscalDocumentId} className={styles.result}>
                    <span className="ds-mono">
                      {row ? documentNumber(row) : item.fiscalDocumentId}
                    </span>
                    <span>{t(`fiscal.bulk.outcome.${item.outcome}`)}</span>
                    {item.status ? <FiscalStatusPill status={item.status} /> : null}
                    {item.errorCode ? <code className="ds-mono">{item.errorCode}</code> : null}
                  </li>
                );
              })}
            </ul>
          </>
        ) : (
          <>
            <p>{t('fiscal.bulk.lead', { limit: BULK_LIMIT })}</p>
            {rejected.length === 0 ? (
              <p className="ds-caption">{t('fiscal.bulk.none')}</p>
            ) : (
              <div className={styles.picker} role="group" aria-label={t('fiscal.bulk.pick')}>
                {rejected.map((r) => (
                  <Checkbox
                    key={r.document.fiscalDocumentId}
                    isSelected={selected.includes(r.document.fiscalDocumentId)}
                    onChange={(on) => {
                      setSelected((prev) =>
                        on
                          ? [...prev, r.document.fiscalDocumentId]
                          : prev.filter((x) => x !== r.document.fiscalDocumentId),
                      );
                    }}
                  >
                    {documentNumber(r)} · {r.document.rejectionCodes?.join(', ') ?? '—'}
                  </Checkbox>
                ))}
              </div>
            )}
            {tried && !countValid ? (
              <p className="ds-caption" role="alert">
                {t('fiscal.bulk.countInvalid', { limit: BULK_LIMIT })}
              </p>
            ) : null}
            <TextField
              label={t('fiscal.actions.reason')}
              helperText={t('fiscal.actions.reasonHelp')}
              isRequired
              multiline
              minRows={2}
              value={reason}
              onChange={setReason}
              errorMessage={tried && !reasonValid ? t('fiscal.actions.reasonInvalid') : undefined}
            />
            {run.isError ? (
              <ProblemBanner error={run.error} title={t('fiscal.bulk.failed')} />
            ) : null}
          </>
        )}
      </div>
    </Dialog>
  );
}
