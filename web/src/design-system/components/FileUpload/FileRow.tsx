import {
  Check,
  CircleAlert,
  Eye,
  File as FileIcon,
  FileImage,
  FileText,
  Mail,
  RotateCcw,
  ShieldCheck,
  Sparkles,
  X,
} from 'lucide-react';
import type { CSSProperties, ReactNode } from 'react';
import { ProgressBar } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import { formatBytes, formatPercent } from '../../../format/numbers';
import { extensionOf, middleTruncate } from './fileValidation';
import styles from './FileUpload.module.css';

export type UploadStatus =
  'queued' | 'uploading' | 'scanning' | 'classifying' | 'done' | 'rejected' | 'failed';

export interface UploadFile {
  id: string;
  name: string;
  size: number;
  type?: string;
  status: UploadStatus;
  /** 0–100 while uploading. */
  progress?: number;
  /** Exact reason for `rejected` (server or client): «Μη επιτρεπτός τύπος αρχείου (.exe)». */
  rejectReason?: string;
  /** AI classification, suggested until a person confirms it: «Δήλωση ατυχήματος · 96 %». */
  classification?: { label: string; confidence?: number };
  /** 40×40 thumbnail for images. */
  thumbnailUrl?: string;
}

function typeIcon(file: UploadFile) {
  const ext = extensionOf(file.name);
  if (file.type?.startsWith('image/') || ['.jpg', '.jpeg', '.png', '.heic', '.gif'].includes(ext)) {
    return FileImage;
  }
  if (ext === '.msg' || ext === '.eml') return Mail;
  if (['.pdf', '.doc', '.docx', '.txt', '.rtf'].includes(ext)) return FileText;
  return FileIcon;
}

interface FileRowProps {
  file: UploadFile;
  isReadOnly: boolean;
  onRemove?: (id: string) => void;
  onRetry?: (id: string) => void;
  onPreview?: (id: string) => void;
}

/** One 48 px file row: type icon, middle-truncated name, size + pipeline status, progress, actions. */
export function FileRow({ file, isReadOnly, onRemove, onRetry, onPreview }: FileRowProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const progress = Math.max(0, Math.min(100, file.progress ?? 0));

  let status: ReactNode;
  switch (file.status) {
    case 'queued':
      status = <span className={styles.status}>{t('fileUpload.queued')}</span>;
      break;
    case 'uploading':
      status = (
        <ProgressBar
          className={cx(styles.progress)}
          value={progress}
          aria-label={t('fileUpload.uploadingName', { name: file.name })}
          valueLabel={t('fileUpload.uploading', {
            percent: formatPercent(progress, { region, fractionDigits: 0 }),
          })}
        >
          {({ valueText }) => (
            <>
              <svg className={styles.ring} viewBox="0 0 16 16" aria-hidden="true">
                <circle className={styles.ringTrack} cx="8" cy="8" r="6" />
                <circle
                  className={styles.ringFill}
                  cx="8"
                  cy="8"
                  r="6"
                  pathLength={100}
                  style={{ '--_progress': String(progress) } as CSSProperties}
                />
              </svg>
              <span className={styles.status}>{valueText}</span>
            </>
          )}
        </ProgressBar>
      );
      break;
    case 'scanning':
      status = (
        <ProgressBar
          className={cx(styles.progress)}
          isIndeterminate
          aria-label={t('fileUpload.scanning')}
        >
          <Icon icon={ShieldCheck} size={14} />
          <span className={styles.status}>{t('fileUpload.scanning')}</span>
        </ProgressBar>
      );
      break;
    case 'classifying':
      status = (
        <ProgressBar
          className={cx(styles.progress)}
          isIndeterminate
          aria-label={t('fileUpload.classifying')}
        >
          <span className={styles.ai}>
            <Icon icon={Sparkles} size={14} />
          </span>
          <span className={styles.status}>{t('fileUpload.classifying')}</span>
        </ProgressBar>
      );
      break;
    case 'done':
      status = (
        <span className={styles.statusRow}>
          <span className={styles.done}>
            <Icon icon={Check} size={14} />
            {t('fileUpload.done')}
          </span>
          {file.classification ? (
            <span className={styles.chip}>
              <Icon icon={Sparkles} size={12} />
              {file.classification.confidence === undefined
                ? file.classification.label
                : t('fileUpload.classification', {
                    label: file.classification.label,
                    confidence: formatPercent(file.classification.confidence, {
                      region,
                      fractionDigits: 0,
                    }),
                  })}
            </span>
          ) : null}
        </span>
      );
      break;
    case 'rejected':
      status = (
        <span className={styles.error}>
          <Icon icon={CircleAlert} size={14} />
          {file.rejectReason ?? t('fileUpload.rejectedGeneric')}
        </span>
      );
      break;
    case 'failed':
      status = (
        <span className={styles.error}>
          <Icon icon={CircleAlert} size={14} />
          {t('fileUpload.failed')}
        </span>
      );
      break;
  }

  return (
    <li className={styles.row} data-status={file.status}>
      {file.thumbnailUrl ? (
        <img className={styles.thumb} src={file.thumbnailUrl} alt="" />
      ) : (
        <span className={styles.typeIcon}>
          <Icon icon={typeIcon(file)} size={20} />
        </span>
      )}
      <span className={styles.info}>
        <span className={styles.name}>
          <span aria-hidden="true">{middleTruncate(file.name)}</span>
          <span className="ds-visually-hidden">{file.name}</span>
        </span>
        <span className={styles.meta}>
          <span className={styles.size}>{formatBytes(file.size, region)}</span>
          {status}
        </span>
      </span>
      <span className={styles.actions}>
        {file.status === 'failed' && onRetry && !isReadOnly ? (
          <Button
            variant="ghost"
            size="sm"
            icon={RotateCcw}
            onPress={() => {
              onRetry(file.id);
            }}
          >
            {t('fileUpload.retry')}
          </Button>
        ) : null}
        {file.status === 'done' && onPreview ? (
          <Button
            variant="ghost"
            size="sm"
            icon={Eye}
            label={t('fileUpload.preview', { name: file.name })}
            onPress={() => {
              onPreview(file.id);
            }}
          />
        ) : null}
        {!isReadOnly && onRemove ? (
          <Button
            variant="ghost"
            size="sm"
            icon={X}
            label={t('fileUpload.remove', { name: file.name })}
            onPress={() => {
              onRemove(file.id);
            }}
          />
        ) : null}
      </span>
    </li>
  );
}
