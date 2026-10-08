import type { InvoiceListPage } from '../../api/types';

export type InvoiceRow = NonNullable<InvoiceListPage['items']>[number];

/** Money in cents: the contract sends decimal strings («494.5100»); sums never go through float addition. */
const cents = (amount: string): number => Math.round(Number(amount) * 100);

const closedStates = new Set(['PAID', 'WRITTEN_OFF', 'REVERSED']);

export interface InvoiceSummary {
  count: number;
  /** Invoices with an open balance, oldest due first. */
  open: InvoiceRow[];
  /** Open invoices past their due date (Athens calendar date), or already OVERDUE. */
  overdue: InvoiceRow[];
  paid: InvoiceRow[];
  /** Most recently issued first. */
  latest: InvoiceRow[];
  totalBilled: number;
  totalPaid: number;
  totalOpen: number;
}

/** Counts and totals of the invoice list, from the real rows only (no estimates, no targets). */
export function summariseInvoices(items: readonly InvoiceRow[], today: string): InvoiceSummary {
  const open = items
    .filter((r) => !closedStates.has(r.invoice.state) && cents(r.invoice.open.amount) > 0)
    .sort((a, b) => a.invoice.dueDate.localeCompare(b.invoice.dueDate));
  const overdue = open.filter((r) => r.invoice.state === 'OVERDUE' || r.invoice.dueDate < today);
  const paid = items.filter((r) => r.invoice.state === 'PAID');
  const latest = [...items].sort((a, b) => b.invoice.issueDate.localeCompare(a.invoice.issueDate));
  const sum = (rows: readonly InvoiceRow[], pick: (r: InvoiceRow) => string) =>
    rows.reduce((total, r) => total + cents(pick(r)), 0) / 100;
  return {
    count: items.length,
    open,
    overdue,
    paid,
    latest,
    totalBilled: sum(items, (r) => r.invoice.total.amount),
    totalPaid: sum(items, (r) => r.invoice.paid.amount),
    totalOpen: sum(open, (r) => r.invoice.open.amount),
  };
}

/** «Καλημέρα» before noon Athens time, «Καλησπέρα» after. */
export function greetingKey(now: Date): 'greeting.morning' | 'greeting.evening' {
  const hour = Number(
    new Intl.DateTimeFormat('en-GB', {
      hour: '2-digit',
      hourCycle: 'h23',
      timeZone: 'Europe/Athens',
    }).format(now),
  );
  return hour < 12 ? 'greeting.morning' : 'greeting.evening';
}
