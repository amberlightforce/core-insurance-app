import { Lock, UploadCloud } from 'lucide-react';
import { useEffect, useId, useRef, useState, type ClipboardEvent } from 'react';
import { Button as AriaButton, DropZone, FileTrigger, type DropItem } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx } from '../../utils/cx';
import { formatBytes } from '../KpiTile/format';
import { FileRow, type UploadFile, type UploadStatus } from './FileRow';
import styles from './FileUpload.module.css';
import {
  DEFAULT_MAX_FILES,
  DEFAULT_MAX_SIZE,
  validateFiles,
  type Rejection,
} from './fileValidation';

export interface FileUploadProps {
  /** Names the zone and the file list, e.g. «Δικαιολογητικά ζημίας». */
  label: string;
  /** Controlled list: the caller performs the upload and moves each file through the pipeline. */
  files: UploadFile[];
  /** Files that passed the client-side checks, in the order they were picked. */
  onAdd: (files: File[]) => void;
  /** Files rejected on the client (they are also listed with the exact reason until removed). */
  onReject?: (rejections: Rejection[]) => void;
  onRemove?: (id: string) => void;
  onRetry?: (id: string) => void;
  onPreview?: (id: string) => void;
  /** Extensions (`.pdf`), MIME types or wildcards (`image/*`). */
  accept?: string[];
  /** Caption of allowed types, e.g. «PDF, JPG, PNG, HEIC, DOCX, MSG»; derived from `accept` by default. */
  acceptLabel?: string;
  /** Bytes; 25 MB by default. */
  maxSize?: number;
  /** 20 by default. */
  maxFiles?: number;
  allowsMultiple?: boolean;
  /** Read-only: the list only, no zone and no remove/retry. */
  isReadOnly?: boolean;
  /** Zone height: 120 px (`md`) or 160 px (`lg`). */
  size?: 'md' | 'lg';
  /** `capture` for mobile cameras. */
  defaultCamera?: 'user' | 'environment';
}

const ANNOUNCE_DELAY_MS = 600;

function isFileItem(item: DropItem): item is Extract<DropItem, { kind: 'file' }> {
  return item.kind === 'file';
}

/**
 * File upload (Part 2 §4.30): RAC DropZone + FileTrigger, Ctrl+V paste, client-side checks for type, size
 * and count, 48 px rows through the pipeline, grouped polite announcements («3 αρχεία έτοιμα, 1 απορρίφθηκε»).
 */
export function FileUpload({
  label,
  files,
  onAdd,
  onReject,
  onRemove,
  onRetry,
  onPreview,
  accept,
  acceptLabel,
  maxSize = DEFAULT_MAX_SIZE,
  maxFiles = DEFAULT_MAX_FILES,
  allowsMultiple = true,
  isReadOnly = false,
  size = 'md',
  defaultCamera,
}: FileUploadProps) {
  const { t, i18n } = useTranslation('ds');
  const region = useRegionFormat();
  const [localRejections, setLocalRejections] = useState<UploadFile[]>([]);
  const limitId = useId();
  const captionId = useId();

  const counted = files.filter((f) => f.status !== 'rejected').length;
  const atLimit = counted >= maxFiles;
  const maxSizeText = formatBytes(maxSize, region);
  const typesText =
    acceptLabel ??
    (accept && accept.length > 0
      ? accept.map((a) => (a.startsWith('.') ? a.slice(1).toUpperCase() : a)).join(', ')
      : null);

  const reasonFor = (rejection: Rejection): string => {
    switch (rejection.code) {
      case 'type':
        return rejection.extension
          ? t('fileUpload.rejectType', { ext: rejection.extension })
          : t('fileUpload.rejectTypeUnknown');
      case 'size':
        return t('fileUpload.rejectSize', { max: maxSizeText });
      case 'limit':
        return t('fileUpload.limit', { max: maxFiles });
      case 'empty':
        return t('fileUpload.rejectEmpty');
    }
  };

  // Grouped announcements: count pipeline outcomes and announce once things settle.
  const tally = useRef({ done: 0, rejected: 0, failed: 0 });
  const previous = useRef(new Map<string, UploadStatus>());
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const latest = useRef({ t, language: i18n.language });
  useEffect(() => {
    latest.current = { t, language: i18n.language };
  });

  const schedule = () => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      const { done, rejected, failed } = tally.current;
      tally.current = { done: 0, rejected: 0, failed: 0 };
      const { t: translate, language } = latest.current;
      const parts = [
        done > 0 ? translate('fileUpload.summaryDone', { count: done }) : null,
        rejected > 0 ? translate('fileUpload.summaryRejected', { count: rejected }) : null,
        failed > 0 ? translate('fileUpload.summaryFailed', { count: failed }) : null,
      ].filter((part): part is string => part !== null);
      if (parts.length === 0) return;
      announce(
        new Intl.ListFormat(language === 'en' ? 'en-GB' : 'el-GR', {
          style: 'short',
          type: 'unit',
        }).format(parts),
      );
    }, ANNOUNCE_DELAY_MS);
  };

  useEffect(() => {
    let changed = false;
    for (const file of files) {
      const before = previous.current.get(file.id);
      if (before !== file.status) {
        if (before !== undefined || file.status !== 'queued') {
          if (file.status === 'done') tally.current.done += 1;
          if (file.status === 'rejected') tally.current.rejected += 1;
          if (file.status === 'failed') tally.current.failed += 1;
          changed = changed || ['done', 'rejected', 'failed'].includes(file.status);
        }
        previous.current.set(file.id, file.status);
      }
    }
    if (changed) schedule();
  });

  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
    },
    [],
  );

  const handle = (picked: File[]) => {
    if (picked.length === 0 || isReadOnly) return;
    const list = allowsMultiple ? picked : picked.slice(0, 1);
    const { accepted, rejected } = validateFiles(list, {
      ...(accept ? { accept } : {}),
      maxSize,
      maxFiles,
      existing: counted,
    });
    if (rejected.length > 0) {
      setLocalRejections((current) => [
        ...current,
        ...rejected.map((r, index) => ({
          id: `rejected-${String(Date.now())}-${String(index)}-${r.file.name}`,
          name: r.file.name,
          size: r.file.size,
          type: r.file.type,
          status: 'rejected' as const,
          rejectReason: reasonFor(r),
        })),
      ]);
      tally.current.rejected += rejected.length;
      schedule();
      onReject?.(rejected);
    }
    if (accepted.length > 0) onAdd(accepted);
  };

  const onDrop = async (items: DropItem[]) => {
    const picked = await Promise.all(items.filter(isFileItem).map((item) => item.getFile()));
    handle(picked);
  };

  const onPaste = (event: ClipboardEvent<HTMLDivElement>) => {
    const pasted = Array.from(event.clipboardData.files);
    if (pasted.length === 0) return;
    event.preventDefault();
    handle(pasted);
  };

  const rows = [...files, ...localRejections];

  return (
    <div className={styles.upload} onPaste={onPaste}>
      {isReadOnly ? null : (
        <DropZone
          className={cx(styles.zone)}
          data-size={size}
          aria-label={label}
          isDisabled={atLimit}
          onDrop={(event) => {
            void onDrop(event.items);
          }}
        >
          <span className={styles.zoneIcon}>
            <Icon icon={atLimit ? Lock : UploadCloud} size={24} />
          </span>
          {atLimit ? (
            <>
              <AriaButton
                className={cx(styles.choose)}
                aria-disabled="true"
                aria-describedby={limitId}
              >
                {t('fileUpload.prompt')}
              </AriaButton>
              <span id={limitId} className={styles.caption} role="status">
                {t('fileUpload.limit', { max: maxFiles })}
              </span>
            </>
          ) : (
            <>
              <FileTrigger
                allowsMultiple={allowsMultiple}
                {...(accept ? { acceptedFileTypes: accept } : {})}
                {...(defaultCamera ? { defaultCamera } : {})}
                onSelect={(list) => {
                  handle(list ? Array.from(list) : []);
                }}
              >
                <AriaButton className={cx(styles.choose)} aria-describedby={captionId}>
                  {t('fileUpload.prompt')}
                </AriaButton>
              </FileTrigger>
              <span id={captionId} className={styles.caption}>
                {typesText
                  ? t('fileUpload.caption', { types: typesText, max: maxSizeText })
                  : t('fileUpload.captionAny', { max: maxSizeText })}
              </span>
            </>
          )}
        </DropZone>
      )}
      {rows.length > 0 ? (
        <ul className={styles.list} aria-label={label}>
          {rows.map((file) => (
            <FileRow
              key={file.id}
              file={file}
              isReadOnly={isReadOnly}
              {...(localRejections.includes(file)
                ? {
                    onRemove: (id: string) => {
                      setLocalRejections((current) => current.filter((r) => r.id !== id));
                    },
                  }
                : onRemove
                  ? { onRemove }
                  : {})}
              {...(onRetry ? { onRetry } : {})}
              {...(onPreview ? { onPreview } : {})}
            />
          ))}
        </ul>
      ) : isReadOnly ? (
        <p className={styles.caption}>{t('fileUpload.none')}</p>
      ) : null}
    </div>
  );
}
