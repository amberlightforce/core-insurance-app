import {
  AlarmClock,
  AlarmClockCheck,
  AlarmClockOff,
  Archive,
  ArrowDownLeft,
  ArrowUpRight,
  Asterisk,
  BadgeCheck,
  Ban,
  BookCheck,
  Calculator,
  Calendar,
  CalendarClock,
  CheckCheck,
  Circle,
  CircleAlert,
  CircleArrowUp,
  CircleCheck,
  CircleDashed,
  CircleDotDashed,
  CircleMinus,
  CircleOff,
  CircleSlash,
  CircleX,
  Clock,
  ClockAlert,
  CloudAlert,
  CornerUpLeft,
  Eraser,
  FileCheck,
  FileClock,
  FileMinus,
  FilePlus,
  FileText,
  FileX,
  Flag,
  FolderCheck,
  FolderOpen,
  FolderSync,
  GitCompare,
  GitMerge,
  Hourglass,
  Inbox,
  Info,
  Landmark,
  Link,
  Link2,
  ListChecks,
  Loader,
  Lock,
  LockKeyhole,
  MailCheck,
  MailWarning,
  MailX,
  MessageSquareCheck,
  OctagonPause,
  Pause,
  Receipt,
  RefreshCw,
  RotateCcw,
  ScanSearch,
  Search,
  Send,
  ShieldAlert,
  ShieldBan,
  ShieldCheck,
  ShieldMinus,
  ShieldOff,
  ShieldQuestion,
  ShieldX,
  SkipForward,
  Sparkle,
  Sparkles,
  SquarePen,
  Stamp,
  Timer,
  TimerOff,
  TriangleAlert,
  Undo2,
  UserCheck,
  WifiOff,
  type LucideIcon,
} from 'lucide-react';

import type { StatusFamily } from '../../tokens';

/**
 * The single source of truth for statuses (Part 1 §2.1.4, DESIGN-A §5.3 "Status map"): every semantic and
 * entity state with its colour family, Lucide icon and label key (labels live in i18n `statusPill.*`).
 * Render statuses only through `<StatusPill>`; never with ad-hoc colours (rule 8).
 */
export interface StatusDefinition {
  family: StatusFamily;
  icon: LucideIcon;
  /** Key in the `ds` namespace. */
  labelKey: string;
  /**
   * Solid "critical" pill: white text on the danger fill. Allowed **only** for `breached`, `conflict` and
   * screening `trueMatch` (DESIGN-A §5.3; TrueMatch is flagged to design as a third solid use).
   */
  solid?: true;
  /** Outline by default (AI-generated). */
  outline?: true;
  /** The icon spins (document rendering). */
  spins?: true;
  /** A flag pill (Referred, Preempted): at most one per row next to the status pill. */
  flag?: true;
}

function status(
  family: StatusFamily,
  icon: LucideIcon,
  labelKey: string,
  extra: Omit<StatusDefinition, 'family' | 'icon' | 'labelKey'> = {},
): StatusDefinition {
  return { family, icon, labelKey, ...extra };
}

/** Semantic states (§3.9.9). `default` and `adverse` are not pills (adverse is text on figures). */
export const semanticStatuses = {
  'read-only': status('neutral', LockKeyhole, 'statusPill.semantic.readOnly'), // glossary: pending
  disabled: status('neutral', CircleSlash, 'statusPill.semantic.disabled'), // glossary: pending
  required: status('danger', Asterisk, 'statusPill.semantic.required'), // glossary: pending
  error: status('danger', CircleAlert, 'statusPill.semantic.error'), // glossary: pending
  warning: status('warning', TriangleAlert, 'statusPill.semantic.warning'), // glossary: pending
  info: status('info', Info, 'statusPill.semantic.info'), // glossary: pending
  success: status('success', CircleCheck, 'statusPill.semantic.success'), // glossary: pending
  'pending-approval': status('plum', Hourglass, 'statusPill.semantic.pendingApproval'), // glossary: pending
  locked: status('neutral', Lock, 'statusPill.semantic.locked'), // glossary: pending
  conflict: status('danger', GitCompare, 'statusPill.semantic.conflict', { solid: true }), // glossary: pending
  stale: status('warning', RefreshCw, 'statusPill.semantic.stale'), // glossary: pending
  offline: status('neutral', WifiOff, 'statusPill.semantic.offline'), // glossary: pending
  degraded: status('neutral', CloudAlert, 'statusPill.semantic.degraded'), // glossary: pending
  'ai-suggested': status('ai', Sparkles, 'statusPill.semantic.aiSuggested'), // glossary: pending
  'ai-generated': status('ai', Sparkle, 'statusPill.semantic.aiGenerated', { outline: true }), // glossary: pending
  overdue: status('danger', ClockAlert, 'statusPill.semantic.overdue'), // glossary: pending
  breached: status('danger', AlarmClockOff, 'statusPill.semantic.breached', { solid: true }), // glossary: pending
} as const satisfies Record<string, StatusDefinition>;

const claimLike = {
  draft: status('neutral', CircleDashed, 'statusPill.claim.draft'), // glossary: pending
  open: status('info', FolderOpen, 'statusPill.claim.open'), // glossary: pending
  closed: status('neutral', FolderCheck, 'statusPill.claim.closed'), // glossary: pending
  reopened: status('info', FolderSync, 'statusPill.claim.reopened'), // glossary: pending
} as const satisfies Record<string, StatusDefinition>;

/** Entity states (§3.2.4). */
export const entityStatuses = {
  job: {
    draft: status('neutral', CircleDashed, 'statusPill.job.draft'), // glossary: pending
    quoted: status('info', FileText, 'statusPill.job.quoted'),
    bound: status('success', BadgeCheck, 'statusPill.job.bound'), // glossary: pending
    withdrawn: status('neutral', Undo2, 'statusPill.job.withdrawn'), // glossary: pending
    declined: status('danger', Ban, 'statusPill.job.declined'), // glossary: pending
    notTaken: status('neutral', CircleSlash, 'statusPill.job.notTaken'), // glossary: pending
    expired: status('neutral', TimerOff, 'statusPill.job.expired'), // glossary: pending
    scheduled: status('teal', CalendarClock, 'statusPill.job.scheduled'), // glossary: pending
    rescinded: status('neutral', RotateCcw, 'statusPill.job.rescinded'), // glossary: pending
    referred: status('plum', Flag, 'statusPill.job.referred', { flag: true }),
    preempted: status('warning', GitMerge, 'statusPill.job.preempted', { flag: true }), // glossary: pending
  },
  policyTerm: {
    scheduled: status('teal', CalendarClock, 'statusPill.policyTerm.scheduled'), // glossary: pending
    inForce: status('success', ShieldCheck, 'statusPill.policyTerm.inForce'), // glossary: pending
    pendingCancellation: status(
      'warning',
      ShieldAlert,
      'statusPill.policyTerm.pendingCancellation', // glossary: pending
    ),
    cancelled: status('danger', ShieldX, 'statusPill.policyTerm.cancelled'),
    expired: status('neutral', ShieldOff, 'statusPill.policyTerm.expired'), // glossary: pending
    nonRenewed: status('neutral', ShieldMinus, 'statusPill.policyTerm.nonRenewed'),
    lapsed: status('ochre', Hourglass, 'statusPill.policyTerm.lapsed'), // glossary: pending
  },
  uwIssue: {
    open: status('warning', CircleAlert, 'statusPill.uwIssue.open'), // glossary: pending
    approved: status('success', CircleCheck, 'statusPill.uwIssue.approved'), // glossary: pending
    approvedWithConditions: status('teal', ListChecks, 'statusPill.uwIssue.approvedWithConditions'), // glossary: pending
    rejected: status('danger', CircleX, 'statusPill.uwIssue.rejected'), // glossary: pending
    invalidated: status('ochre', CircleOff, 'statusPill.uwIssue.invalidated'), // glossary: pending
    closed: status('neutral', CircleMinus, 'statusPill.uwIssue.closed'), // glossary: pending
  },
  claim: claimLike,
  exposure: claimLike,
  transactionSet: {
    draft: status('neutral', CircleDashed, 'statusPill.transactionSet.draft'), // glossary: pending
    submitted: status('plum', Send, 'statusPill.transactionSet.submitted'), // glossary: pending
    pendingApproval: status('plum', UserCheck, 'statusPill.transactionSet.pendingApproval'), // glossary: pending
    approved: status('success', CircleCheck, 'statusPill.transactionSet.approved'), // glossary: pending
    rejected: status('danger', CircleX, 'statusPill.transactionSet.rejected'), // glossary: pending
    posted: status('teal', BookCheck, 'statusPill.transactionSet.posted'), // glossary: pending
  },
  invoice: {
    planned: status('neutral', Calendar, 'statusPill.invoice.planned'), // glossary: pending
    billed: status('info', Receipt, 'statusPill.invoice.billed'), // glossary: pending
    due: status('info', CalendarClock, 'statusPill.invoice.due'), // glossary: pending
    partiallyPaid: status('teal', CircleDotDashed, 'statusPill.invoice.partiallyPaid'), // glossary: pending
    paid: status('success', CircleCheck, 'statusPill.invoice.paid'), // glossary: pending
    overdue: status('danger', ClockAlert, 'statusPill.invoice.overdue'), // glossary: pending
    writtenOff: status('neutral', Eraser, 'statusPill.invoice.writtenOff'), // glossary: pending
    reversed: status('neutral', Undo2, 'statusPill.invoice.reversed'), // glossary: pending
  },
  paymentIn: {
    received: status('info', ArrowDownLeft, 'statusPill.paymentIn.received'), // glossary: pending
    allocated: status('success', Link, 'statusPill.paymentIn.allocated'), // glossary: pending
    partiallyAllocated: status('teal', Link2, 'statusPill.paymentIn.partiallyAllocated'), // glossary: pending
    suspense: status('warning', Inbox, 'statusPill.paymentIn.suspense'), // glossary: pending
    reversed: status('neutral', Undo2, 'statusPill.paymentIn.reversed'), // glossary: pending
    refunded: status('neutral', ArrowUpRight, 'statusPill.paymentIn.refunded'), // glossary: pending
  },
  disbursement: {
    requested: status('neutral', FilePlus, 'statusPill.disbursement.requested'), // glossary: pending
    pendingApproval: status('plum', UserCheck, 'statusPill.disbursement.pendingApproval'), // glossary: pending
    approved: status('success', CircleCheck, 'statusPill.disbursement.approved'), // glossary: pending
    released: status('teal', Send, 'statusPill.disbursement.released'), // glossary: pending
    issued: status('teal', Landmark, 'statusPill.disbursement.issued'), // glossary: pending
    cleared: status('success', BadgeCheck, 'statusPill.disbursement.cleared'), // glossary: pending
    rejected: status('danger', CircleX, 'statusPill.disbursement.rejected'), // glossary: pending
    stopped: status('warning', OctagonPause, 'statusPill.disbursement.stopped'), // glossary: pending
    voided: status('neutral', CircleSlash, 'statusPill.disbursement.voided'), // glossary: pending
    returned: status('danger', CornerUpLeft, 'statusPill.disbursement.returned'), // glossary: pending
  },
  activity: {
    open: status('info', Circle, 'statusPill.activity.open'), // glossary: pending
    completed: status('success', CircleCheck, 'statusPill.activity.completed'), // glossary: pending
    skipped: status('neutral', SkipForward, 'statusPill.activity.skipped'), // glossary: pending
    cancelled: status('neutral', CircleSlash, 'statusPill.activity.cancelled'), // glossary: pending
  },
  outboundDocument: {
    requested: status('neutral', FileClock, 'statusPill.outboundDocument.requested'), // glossary: pending
    rendering: status('info', Loader, 'statusPill.outboundDocument.rendering', { spins: true }), // glossary: pending
    rendered: status('success', FileCheck, 'statusPill.outboundDocument.rendered'), // glossary: pending
    failed: status('danger', FileX, 'statusPill.outboundDocument.failed'), // glossary: pending
    superseded: status('neutral', FileMinus, 'statusPill.outboundDocument.superseded'), // glossary: pending
  },
  delivery: {
    pending: status('neutral', Clock, 'statusPill.delivery.pending'), // glossary: pending
    sent: status('info', Send, 'statusPill.delivery.sent'), // glossary: pending
    delivered: status('success', MailCheck, 'statusPill.delivery.delivered'), // glossary: pending
    failed: status('danger', MailX, 'statusPill.delivery.failed'), // glossary: pending
    bounced: status('danger', MailWarning, 'statusPill.delivery.bounced'), // glossary: pending
  },
  fiscalDocument: {
    pending: status('neutral', Clock, 'statusPill.fiscalDocument.pending'), // glossary: pending
    submitted: status('plum', Send, 'statusPill.fiscalDocument.submitted'), // glossary: pending
    registered: status('success', Stamp, 'statusPill.fiscalDocument.registered'),
    rejected: status('danger', CircleX, 'statusPill.fiscalDocument.rejected'), // glossary: pending
    cancelled: status('neutral', CircleSlash, 'statusPill.fiscalDocument.cancelled'), // glossary: pending
  },
  clockInstance: {
    running: status('info', Timer, 'statusPill.clockInstance.running'), // glossary: pending
    paused: status('neutral', Pause, 'statusPill.clockInstance.paused'), // glossary: pending
    warned: status('warning', AlarmClock, 'statusPill.clockInstance.warned'), // glossary: pending
    met: status('success', AlarmClockCheck, 'statusPill.clockInstance.met'), // glossary: pending
    breached: status('danger', AlarmClockOff, 'statusPill.clockInstance.breached', { solid: true }), // glossary: pending
    cancelled: status('neutral', CircleSlash, 'statusPill.clockInstance.cancelled'), // glossary: pending
  },
  complaint: {
    received: status('info', Inbox, 'statusPill.complaint.received'), // glossary: pending
    acknowledged: status('info', CheckCheck, 'statusPill.complaint.acknowledged'), // glossary: pending
    underInvestigation: status('plum', Search, 'statusPill.complaint.underInvestigation'), // glossary: pending
    answered: status('success', MessageSquareCheck, 'statusPill.complaint.answered'), // glossary: pending
    escalated: status('danger', CircleArrowUp, 'statusPill.complaint.escalated'), // glossary: pending
    closed: status('neutral', CircleMinus, 'statusPill.complaint.closed'),
  },
  cession: {
    calculated: status('info', Calculator, 'statusPill.cession.calculated'), // glossary: pending
    exception: status('warning', TriangleAlert, 'statusPill.cession.exception'), // glossary: pending
    posted: status('teal', BookCheck, 'statusPill.cession.posted'), // glossary: pending
    reversed: status('neutral', Undo2, 'statusPill.cession.reversed'), // glossary: pending
  },
  productVersion: {
    draft: status('neutral', CircleDashed, 'statusPill.productVersion.draft'), // glossary: pending
    submitted: status('plum', Send, 'statusPill.productVersion.submitted'), // glossary: pending
    approved: status('success', CircleCheck, 'statusPill.productVersion.approved'), // glossary: pending
    locked: status('brand', Lock, 'statusPill.productVersion.locked'), // glossary: pending
    retired: status('neutral', Archive, 'statusPill.productVersion.retired'), // glossary: pending
  },
  aiRecommendation: {
    proposed: status('ai', Sparkles, 'statusPill.aiRecommendation.proposed'), // glossary: pending
    accepted: status('success', CircleCheck, 'statusPill.aiRecommendation.accepted'), // glossary: pending
    edited: status('success', SquarePen, 'statusPill.aiRecommendation.edited'), // glossary: pending
    rejected: status('neutral', CircleX, 'statusPill.aiRecommendation.rejected'), // glossary: pending
    expired: status('neutral', TimerOff, 'statusPill.aiRecommendation.expired'), // glossary: pending
  },
  screeningResult: {
    clear: status('success', ShieldCheck, 'statusPill.screeningResult.clear'), // glossary: pending
    potentialHit: status('warning', ScanSearch, 'statusPill.screeningResult.potentialHit'), // glossary: pending
    falsePositive: status('neutral', ShieldQuestion, 'statusPill.screeningResult.falsePositive'), // glossary: pending
    trueMatch: status('danger', ShieldBan, 'statusPill.screeningResult.trueMatch', { solid: true }), // glossary: pending
  },
} as const satisfies Record<string, Record<string, StatusDefinition>>;

export type SemanticStatus = keyof typeof semanticStatuses;
export type StatusEntity = keyof typeof entityStatuses;
export type EntityState<E extends StatusEntity> = keyof (typeof entityStatuses)[E] & string;

/** `{ entity, state }` pairs, typed so that an invalid pair fails the type check. */
export type EntityStatusRef = {
  [E in StatusEntity]: { entity: E; state: EntityState<E>; semantic?: never };
}[StatusEntity];

export interface SemanticStatusRef {
  semantic: SemanticStatus;
  entity?: never;
  state?: never;
}

export type StatusRef = EntityStatusRef | SemanticStatusRef;

/** Looks up a status. Throws on an unknown pair (only reachable from untyped data, e.g. an API value). */
export function getStatusDefinition(ref: StatusRef): StatusDefinition {
  if (ref.semantic !== undefined) return semanticStatuses[ref.semantic];
  const states = entityStatuses[ref.entity] as Record<string, StatusDefinition>;
  const found = states[ref.state];
  if (!found) throw new RangeError(`Unknown status ${ref.entity}.${ref.state}`);
  return found;
}

/** A stable id for a status, used to detect changes (MI-15). */
export function statusId(ref: StatusRef): string {
  return ref.semantic !== undefined ? `semantic.${ref.semantic}` : `${ref.entity}.${ref.state}`;
}

/** Narrows untyped data (an API string) to a typed status reference, or `null` if the pair is unknown. */
export function toEntityStatus(entity: StatusEntity, state: string): EntityStatusRef | null {
  const states = entityStatuses[entity] as Record<string, StatusDefinition>;
  return state in states ? ({ entity, state } as EntityStatusRef) : null;
}

/** Every status definition (used by tests and the catalogue story). */
export function allStatusDefinitions(): {
  id: string;
  ref: StatusRef;
  definition: StatusDefinition;
}[] {
  const out: { id: string; ref: StatusRef; definition: StatusDefinition }[] = [];
  for (const [semantic, definition] of Object.entries(semanticStatuses)) {
    const ref = { semantic: semantic as SemanticStatus };
    out.push({ id: statusId(ref), ref, definition });
  }
  for (const [entity, states] of Object.entries(entityStatuses)) {
    for (const [state, definition] of Object.entries(states as Record<string, StatusDefinition>)) {
      const ref = { entity, state } as EntityStatusRef;
      out.push({ id: statusId(ref), ref, definition });
    }
  }
  return out;
}
