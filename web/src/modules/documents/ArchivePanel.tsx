import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../api/idempotency';
import { Banner, Button, KeyValueList, announce } from '../../design-system';
import { ProblemBanner } from '../staff/ProblemBanner';
import styles from '../staff/staff.module.css';
import { downloadArchive, useArchiveVerify, useEvidenceExport, type DocumentView } from './api';
import local from './documents.module.css';

/** Stored letter: content hash, integrity check (doc.Archive.verify), download and the sealed evidence pack. */
export function ArchivePanel({ document }: { document: DocumentView }) {
  const { t } = useTranslation('documents');
  const verify = useArchiveVerify();
  const exportPack = useEvidenceExport();
  const { keyFor, release } = useIdempotencyKey();
  const [downloadError, setDownloadError] = useState<string | null>(null);
  const [downloading, setDownloading] = useState(false);

  const download = async (archiveId: string, fileName: string) => {
    setDownloadError(null);
    setDownloading(true);
    try {
      await downloadArchive(archiveId, fileName);
    } catch {
      setDownloadError(t('archive.downloadFailed'));
    } finally {
      setDownloading(false);
    }
  };

  const result = verify.data;
  const exported = exportPack.data;
  return (
    <div className={styles.stack}>
      <KeyValueList
        aria-label={t('archive.title')}
        items={[
          { id: 'archive', label: t('detail.archive'), value: document.archiveId, kind: 'mono' },
          { id: 'hash', label: t('detail.hash'), value: document.contentHash, kind: 'mono' },
        ]}
      />
      <div className={local.actionRow}>
        <Button
          variant="secondary"
          isLoading={verify.isPending}
          onPress={() => {
            verify.mutate(document.archiveId, {
              onSuccess: (r) => {
                announce(r.result === 'OK' ? t('archive.verifyOk') : t('archive.verifyTampered'));
              },
            });
          }}
        >
          {t('archive.verify')}
        </Button>
        <Button
          variant="secondary"
          isLoading={downloading}
          onPress={() => {
            void download(document.archiveId, `${document.documentNumber}.html`);
          }}
        >
          {t('archive.download')}
        </Button>
        <Button
          variant="secondary"
          isLoading={exportPack.isPending}
          onPress={() => {
            const payload = { documentId: document.documentId };
            exportPack.mutate(
              { documentId: document.documentId, key: keyFor(payload) },
              { onSuccess: release },
            );
          }}
        >
          {t('archive.export')}
        </Button>
      </div>
      <div className={local.verifyResult} aria-live="polite">
        {result ? (
          result.result === 'OK' ? (
            <Banner variant="success" live="none" title={t('archive.verifyOk')}>
              {t('archive.verifyOkBody')}
            </Banner>
          ) : (
            <Banner variant="danger" live="none" title={t('archive.verifyTampered')}>
              <p>{t('archive.verifyTamperedBody')}</p>
              <p className="ds-mono">{result.computedHash}</p>
            </Banner>
          )
        ) : null}
        {verify.isError ? (
          <ProblemBanner error={verify.error} title={t('archive.verifyFailed')} />
        ) : null}
        {exported ? (
          <Banner variant="success" live="none" title={t('archive.exported')}>
            <p>{t('archive.exportedBody', { count: exported.documentCount })}</p>
            <Button
              variant="secondary"
              size="sm"
              onPress={() => {
                void download(exported.archiveId, `evidence-${document.documentNumber}.zip`);
              }}
            >
              {t('archive.downloadPack')}
            </Button>
          </Banner>
        ) : null}
        {exportPack.isError ? (
          <ProblemBanner error={exportPack.error} title={t('archive.exportFailed')} />
        ) : null}
        {downloadError ? (
          <Banner variant="danger" live="none" title={downloadError}>
            {t('archive.downloadFailedBody')}
          </Banner>
        ) : null}
      </div>
    </div>
  );
}
