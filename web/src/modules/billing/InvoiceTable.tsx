import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import type { InvoiceListPage } from '../../api/types';
import {
  EmptyState,
  dateColumn,
  identifierColumn,
  moneyColumn,
  statusColumn,
  type DataColumn,
} from '../../design-system';
import { SimpleTable } from '../staff/SimpleTable';
import { FiscalStatusPill } from './FiscalStatusPill';
import { InvoiceStatePill } from './InvoiceStatePill';

/** The fiscal status code of a row; absent when billing has not reported one yet. */
const fiscalOf = (row: Row): string | undefined =>
  (row.fiscalStatus as { status?: string } | undefined)?.status;

type Row = NonNullable<InvoiceListPage['items']>[number];

export interface InvoiceTableProps {
  items: readonly Row[];
  label: string;
  /** Short fiscal-status pill for narrow cards. */
  compact?: boolean;
}

/** Invoices with their state and the fiscal-document marker; Enter opens one. */
export function InvoiceTable({ items, label, compact = false }: InvoiceTableProps) {
  const { t } = useTranslation('billing');
  const navigate = useNavigate();
  const columns = useMemo<DataColumn<Row>[]>(
    () => [
      identifierColumn<Row>('number', t('invoice.columns.number'), (r) => r.invoice.invoiceNumber, {
        size: 160,
      }),
      statusColumn<Row>(
        'state',
        t('invoice.columns.state'),
        (r) => r.invoice.state,
        (r) => <InvoiceStatePill state={r.invoice.state} />,
      ),
      dateColumn<Row>('issue', t('invoice.columns.issueDate'), (r) => r.invoice.issueDate),
      dateColumn<Row>('due', t('invoice.columns.dueDate'), (r) => r.invoice.dueDate),
      moneyColumn<Row>('total', t('invoice.columns.total'), (r) => r.invoice.total.amount, {
        currency: 'EUR',
      }),
      moneyColumn<Row>('open', t('invoice.columns.open'), (r) => r.invoice.open.amount, {
        currency: 'EUR',
      }),
      statusColumn<Row>(
        'fiscal',
        t('invoice.columns.fiscal'),
        (r) => fiscalOf(r),
        (r) => <FiscalStatusPill status={fiscalOf(r)} compact={compact} />,
      ),
    ],
    [t, compact],
  );
  return (
    <SimpleTable<Row>
      aria-label={label}
      columns={columns}
      data={items}
      getRowId={(r) => r.invoice.invoiceId}
      onOpen={(r) => {
        void navigate(`/billing/invoices/${r.invoice.invoiceId}`);
      }}
      emptyState={
        <EmptyState
          kind="first-use"
          headingLevel={3}
          headline={t('invoice.emptyTitle')}
          description={t('invoice.emptyBody')}
        />
      }
    />
  );
}
