import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import {
  identifierColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { SimpleTable } from '../../staff/SimpleTable';
import { useFormat } from '../../staff/useFormat';
import { documentNumber, type FiscalDocument } from './api';
import { FiscalStatusPill } from './StatusPills';

/** The shared fiscal-document grid (list and breaks): number, role, status, issue date, total, MARK, rejections. */
export function FiscalDocumentsTable({
  rows,
  label,
  empty,
  showRejections = false,
}: {
  rows: FiscalDocument[];
  label: string;
  empty: string;
  showRejections?: boolean;
}) {
  const { t } = useTranslation('compliance');
  const fmt = useFormat();
  const navigate = useNavigate();

  const columns = useMemo<DataColumn<FiscalDocument>[]>(() => {
    const base: DataColumn<FiscalDocument>[] = [
      identifierColumn<FiscalDocument>('number', t('fiscal.columns.number'), documentNumber, {
        size: 170,
      }),
      textColumn<FiscalDocument>(
        'role',
        t('fiscal.columns.role'),
        (d) => t(`fiscal.role.${d.document.role}`, { defaultValue: d.document.role }),
        { size: 130 },
      ),
      textColumn<FiscalDocument>(
        'source',
        t('fiscal.columns.source'),
        (d) => d.document.sourceType,
        {
          size: 160,
        },
      ),
      statusColumn<FiscalDocument>(
        'status',
        t('fiscal.columns.status'),
        (d) => d.document.status,
        (d) => <FiscalStatusPill status={d.document.status} />,
        { size: 140 },
      ),
      textColumn<FiscalDocument>(
        'issued',
        t('fiscal.columns.issued'),
        (d) => fmt.date(d.document.issueDate),
        { size: 120 },
      ),
      textColumn<FiscalDocument>(
        'total',
        t('fiscal.columns.total'),
        (d) => fmt.money(d.document.total),
        {
          size: 140,
        },
      ),
      identifierColumn<FiscalDocument>(
        'mark',
        t('fiscal.columns.mark'),
        (d) => d.identifiers.mark ?? '—',
        { size: 170 },
      ),
    ];
    if (showRejections) {
      base.push(
        textColumn<FiscalDocument>(
          'rejections',
          t('fiscal.columns.rejections'),
          (d) => (d.document.rejectionCodes?.length ? d.document.rejectionCodes.join(', ') : '—'),
          { size: 200 },
        ),
      );
    }
    return base;
  }, [t, fmt, showRejections]);

  return (
    <SimpleTable<FiscalDocument>
      aria-label={label}
      columns={columns}
      data={rows}
      getRowId={(d) => d.document.fiscalDocumentId}
      getRowLabel={documentNumber}
      onOpen={(d) => {
        void navigate(`/compliance/fiscal/${d.document.fiscalDocumentId}`);
      }}
      emptyState={<p className="ds-caption">{empty}</p>}
    />
  );
}
