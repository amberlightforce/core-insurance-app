import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { PackActivationPreview, PackListItem } from '../../../api/types';
import { useIdempotencyKey } from '../../../api/idempotency';
import { Dialog, Select, TextField, announce } from '../../../design-system';
import { useFormat } from '../../staff/useFormat';
import { requestActivation, useRefreshPacks, type ActivationKind } from './api';
import { packErrorText } from './errors';
import { PreviewPanel } from './PreviewPanel';
import { reasonMax, reasonMin, targetVersions } from './lifecycle';
import styles from './Packs.module.css';

export interface ActivationDialogProps {
  pack: Pick<PackListItem, 'pack' | 'versions' | 'activeVersions'>;
  kind: ActivationKind;
  isOpen: boolean;
  onClose: () => void;
  /** Called with the new activation id once the request is submitted (PENDING_APPROVAL). */
  onSubmitted: (activationId: string) => void;
}

/**
 * «Επαναφορά έκδοσης…» (Platform.ReleaseManager) and «Ενεργοποίηση έκδοσης…» share one dialog: choose the legal
 * entity and the target version, give a reason (≥ 20 characters), **preview** (the dry run: window, hashes issued,
 * key differences), then submit. Submit stays disabled until the preview of exactly these inputs has been seen.
 * The request needs a checker (maker-checker, D-SL5-08); nothing takes effect here.
 */
export function ActivationDialog({
  pack,
  kind,
  isOpen,
  onClose,
  onSubmitted,
}: ActivationDialogProps) {
  const { t } = useTranslation('market');
  const fmt = useFormat();
  const { keyFor, release } = useIdempotencyKey();
  const refresh = useRefreshPacks();

  const entities = pack.activeVersions.map((a) => a.legalEntity);
  const [legalEntity, setLegalEntity] = useState<string>(entities[0] ?? '');
  const active = pack.activeVersions.find((a) => a.legalEntity === legalEntity);
  const targets = targetVersions(pack.versions, active);
  const [version, setVersion] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [preview, setPreview] = useState<{ fingerprint: string; value: PackActivationPreview } | null>(
    null,
  );
  const [error, setError] = useState<string | undefined>();
  const [busy, setBusy] = useState(false);

  const trimmed = reason.trim();
  const reasonValid = trimmed.length >= reasonMin && trimmed.length <= reasonMax;
  const ready = legalEntity.trim() !== '' && version !== null && reasonValid;
  const input = {
    kind,
    pack: pack.pack,
    legalEntity: legalEntity.trim(),
    version: version ?? '',
    reason: trimmed,
  };
  const fingerprint = JSON.stringify(input);
  // The preview counts only for the inputs it was run with: any edit asks for a new one.
  const previewed = preview?.fingerprint === fingerprint ? preview.value : null;

  const reasonHint = (() => {
    if (trimmed.length === 0) return undefined;
    if (trimmed.length < reasonMin) return t('packs.dialog.reasonShort', { min: reasonMin });
    return undefined;
  })();

  const runPreview = async () => {
    setError(undefined);
    setBusy(true);
    try {
      const response = await requestActivation(input, true, keyFor({ ...input, dry: true }));
      if (response.preview) setPreview({ fingerprint, value: response.preview });
    } catch (e) {
      setError(packErrorText(t, e));
    } finally {
      setBusy(false);
    }
  };

  const submit = async () => {
    setError(undefined);
    setBusy(true);
    try {
      const response = await requestActivation(input, false, keyFor({ ...input, dry: false }));
      release();
      announce(t('packs.dialog.submitted'));
      void refresh();
      if (response.activationId) onSubmitted(response.activationId);
      onClose();
    } catch (e) {
      setError(packErrorText(t, e));
    } finally {
      setBusy(false);
    }
  };

  const title = kind === 'ROLLBACK' ? t('packs.dialog.rollbackTitle') : t('packs.dialog.activateTitle');
  const noTargets = targets.length === 0;

  return (
    <Dialog
      title={`${title} · ${pack.pack}`}
      size="lg"
      tone={kind === 'ROLLBACK' ? 'warning' : 'brand'}
      isOpen={isOpen}
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      onCancel={onClose}
      closeOnAction={false}
      isBusy={busy}
      {...(error ? { error } : {})}
      cancelLabel={t('packs.dialog.cancel')}
      secondaryActions={[
        {
          label: t('packs.dialog.preview'),
          variant: 'secondary',
          onAction: runPreview,
          ...(!ready ? { disabledReason: t('packs.dialog.previewNeeds') } : {}),
        },
      ]}
      primaryAction={{
        label: t('packs.dialog.submit'),
        variant: 'primary',
        onAction: submit,
        ...(!previewed ? { disabledReason: t('packs.dialog.submitNeedsPreview') } : {}),
      }}
    >
      <div className={styles.dialogBody}>
        <p className="ds-caption">{t('packs.dialog.makerChecker')}</p>
        {entities.length > 0 ? (
          <Select
            label={t('packs.dialog.entity')}
            options={entities.map((e) => ({ id: e, label: e }))}
            value={legalEntity}
            onChange={(value) => {
              setLegalEntity(value ?? '');
              setVersion(null);
            }}
          />
        ) : (
          <TextField
            label={t('packs.dialog.entity')}
            helperText={t('packs.dialog.entityHelp')}
            value={legalEntity}
            onChange={setLegalEntity}
            maxLength={32}
          />
        )}
        <p className="ds-caption">
          {t('packs.dialog.currentVersion')}:{' '}
          <span className="ds-mono">{active?.version ?? t('packs.dialog.none')}</span>
        </p>
        <Select
          label={kind === 'ROLLBACK' ? t('packs.dialog.rollbackTo') : t('packs.dialog.activateVersion')}
          options={targets.map((v) => ({
            id: v.version,
            label: `${v.version} · ${t('packs.dialog.publishedOn', { date: fmt.date(v.publishedAt) })}`,
          }))}
          value={version}
          onChange={setVersion}
          {...(noTargets ? { disabledReason: t('packs.dialog.noTargets') } : {})}
        />
        <TextField
          label={t('packs.dialog.reason')}
          multiline
          minRows={3}
          maxLength={reasonMax}
          showCount
          isRequired
          helperText={t('packs.dialog.reasonHelp', { min: reasonMin })}
          value={reason}
          onChange={setReason}
          errorMessage={reasonHint}
        />
        {previewed ? (
          <PreviewPanel preview={previewed} />
        ) : (
          <p className="ds-caption">{t('packs.dialog.previewHint')}</p>
        )}
      </div>
    </Dialog>
  );
}
