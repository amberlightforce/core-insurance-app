/**
 * Wire shapes of the finance operations screens. The FIN contract (fin.yaml) leaves most of these bodies
 * `unknown`; the read endpoints that are served (trial balance, intake exceptions, periods) define their shapes in
 * `FinanceOpsReader.cs`, the rest follow the same conventions and are `TODO(hardening)` until the backend lands.
 */

export interface TrialBalanceRow {
  book: string;
  account: string;
  nameEl: string;
  nameEn: string;
  debit: string;
  credit: string;
  balance: string;
}

export interface TrialBalance {
  period: string;
  book: string | null;
  currency: string;
  balances: TrialBalanceRow[];
  totalDebit: string;
  totalCredit: string;
}

export interface IntakeException {
  eventId: string;
  sourceModule: string;
  eventType: string;
  aggregateType: string;
  aggregateId: string;
  reason: string;
  detail: string | null;
  attempts: number;
  receivedAt: string;
  updatedAt: string;
}

export interface FinancePeriod {
  period: string;
  state: string;
  closedForPosting: boolean;
}

export type CloseTaskCode =
  'INTAKE_EXCEPTIONS' | 'RECONCILIATIONS' | 'EARNING_RUN' | 'MANUAL_JOURNALS';

/** One close checklist task (fin.Close.tasks). */
export interface CloseTask {
  task: CloseTaskCode;
  status: 'OPEN' | 'SIGNED_OFF';
  signOffRole: string;
  blocker?: string | null;
  signedOffBy?: string | null;
  signedOffAt?: string | null;
  reason?: string | null;
}

/** A period with its close checklist: the response of every fin.Close operation. */
export interface ClosePeriodView {
  period: string;
  state: 'OPEN' | 'SOFT_CLOSED' | 'LOCKED';
  closedForPosting: boolean;
  tasks: CloseTask[];
  softClosedBy?: string | null;
  lockedBy?: string | null;
  reopenRequestedBy?: string | null;
  reopenRequestedAt?: string | null;
  reopenReason?: string | null;
}

export interface Page<T> {
  items: T[];
  nextCursor: string | null;
  limit?: number;
}

export type ManualJournalStatus =
  'DRAFT' | 'SUBMITTED' | 'APPROVED' | 'REJECTED' | 'WITHDRAWN' | 'POSTED';

export interface ManualJournalLine {
  account: string;
  side: 'DEBIT' | 'CREDIT';
  amount: string;
}

export interface ManualJournal {
  id: string;
  number?: string;
  status: ManualJournalStatus;
  description: string;
  book: string;
  accountingDate: string;
  currency: string;
  lines: ManualJournalLine[];
  evidenceRef: string;
  createdBy?: string;
  submittedBy?: string;
  submittedAt?: string;
  decidedBy?: string;
  decisionReason?: string;
}

/** fin.ManualJournal.list item and get result (the contract's `manualJournal` + `postedIds`). */
export interface ManualJournalEnvelope {
  manualJournal: ManualJournal;
  postedIds?: string[];
}

export interface ManualJournalSubmitRequest {
  description: string;
  book: string;
  accountingDate: string;
  currency: string;
  lines: ManualJournalLine[];
  evidence: { reference: string };
}

export interface ManualJournalDecideRequest {
  id: string;
  decision: 'APPROVE' | 'REJECT';
  reason?: string;
}

/** fin.Earning.status: every member is optional until the backend defines the run record. */
export interface EarningRun {
  id?: string;
  state?: string;
  asOf?: string;
  startedAt?: string;
  finishedAt?: string;
  policiesProcessed?: number;
}

export interface EarningStatus {
  run?: EarningRun | null;
  results?: unknown;
}
