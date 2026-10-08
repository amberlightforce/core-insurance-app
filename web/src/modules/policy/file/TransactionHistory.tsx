import { useId, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import {
  Button,
  EmptyState,
  SkeletonBlock,
  StatusPill,
  identifierColumn,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { problemOf } from '../../staff/problem';
import { SimpleTable } from '../../staff/SimpleTable';
import { useFormat } from '../../staff/useFormat';
import {
  useTermTimeline,
  type ChargeLineModel,
  type PolicyFileResponse,
  type TermViewModel,
  type TimelineTransaction,
} from './api';
import { chargesOf, historyKind, orderHistory, type HistoryKind } from './model';
import styles from './PolicyFile.module.css';

const familyOf: Record<HistoryKind, 'success' | 'info' | 'brand' | 'warning' | 'neutral'> = {
  NEW_BUSINESS: 'success',
  CHANGE: 'info',
  CANCELLATION: 'warning',
  RENEWAL: 'brand',
  OTHER: 'neutral',
};

const semanticOf: Record<HistoryKind, 'success' | 'info' | 'warning' | 'read-only'> = {
  NEW_BUSINESS: 'success',
  CHANGE: 'info',
  CANCELLATION: 'warning',
  RENEWAL: 'info',
  OTHER: 'read-only',
};

/** Charge lines of one transaction: element × charge type, with the provisional tax badge (REQ-POL-128). */
function ChargesTable({ charges, label }: { charges: ChargeLineModel[]; label: string }) {
  const { t } = useTranslation('policy');
  const columns = useMemo<DataColumn<ChargeLineModel>[]>(
    () => [
      textColumn<ChargeLineModel>(
        'element',
        t('file.charges.element'),
        (c) => `${c.elementLocator} · ${c.coverageCode}`,
      ),
      identifierColumn<ChargeLineModel>('chargeType', t('file.charges.type'), (c) => c.chargeType, {
        size: 130,
      }),
      moneyColumn<ChargeLineModel>('amount', t('file.charges.amount'), (c) => c.amount.amount, {
        currency: 'EUR',
      }),
      statusColumn<ChargeLineModel>(
        'legal',
        t('file.charges.legalStatus'),
        (c) => c.legalStatus ?? null,
        (c) =>
          c.provisional === true ? (
            <StatusPill
              semantic="warning"
              text={t('file.charges.provisional')}
              announceChanges={false}
            />
          ) : c.legalStatus ? (
            <span>{c.legalStatus}</span>
          ) : null,
        { size: 200 },
      ),
    ],
    [t],
  );
  return (
    <SimpleTable<ChargeLineModel>
      aria-label={label}
      columns={columns}
      data={charges}
      getRowId={(c) =>
        c.chargeId ??
        `${c.elementLocator}:${c.coverageCode}:${c.chargeType}:${c.transactionId ?? ''}`
      }
    />
  );
}

function HistoryRow({
  row,
  term,
  charges,
}: {
  row: TimelineTransaction;
  term: TermViewModel;
  charges: ChargeLineModel[];
}) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const [open, setOpen] = useState(false);
  const panelId = useId();
  const kind = historyKind(row.kind, term.termNumber, row.sequence);
  return (
    <li data-family={familyOf[kind]}>
      <div className={styles.eventRow}>
        <span className={styles.eventText}>
          <StatusPill
            semantic={semanticOf[kind]}
            text={t(`file.kind.${kind}`)}
            announceChanges={false}
          />
          <span className="ds-mono">{`#${String(row.sequence)}`}</span>
          {row.reversed ? (
            <StatusPill
              semantic="read-only"
              text={t('file.history.reversed')}
              announceChanges={false}
            />
          ) : null}
        </span>
        <span className={styles.amount}>
          <span className={row.reversed ? styles.reversed : undefined}>
            {fmt.money(row.premiumChange)}
          </span>
        </span>
      </div>
      <span className={styles.eventMeta}>
        {t('file.history.effective', { date: fmt.date(row.effectiveAt) })}
        {' · '}
        {t('file.history.recorded', { date: fmt.dateTime(row.recordedAt) })}
      </span>
      {charges.length > 0 ? (
        <>
          <div>
            <Button
              variant="link"
              aria-expanded={open}
              aria-controls={panelId}
              onPress={() => {
                setOpen((v) => !v);
              }}
            >
              {open
                ? t('file.history.hideCharges')
                : t('file.history.showCharges', { count: charges.length })}
            </Button>
          </div>
          {open ? (
            <div id={panelId} className={styles.charges}>
              <ChargesTable
                charges={charges}
                label={t('file.history.chargesOf', { sequence: row.sequence })}
              />
            </div>
          ) : null}
        </>
      ) : null}
    </li>
  );
}

export interface HistoryGroupInput {
  term: TermViewModel;
  policy: PolicyFileResponse;
  validAt: string;
}

/** One term's transactions, newest first. The timeline is the source; the policy's own transactions are the fallback. */
function TermHistoryGroup({ policyId, group }: { policyId: string; group: HistoryGroupInput }) {
  const { t } = useTranslation('policy');
  const { term, policy, validAt } = group;
  const timeline = useTermTimeline(policyId, term.termId, validAt);
  const rows = useMemo<TimelineTransaction[]>(() => {
    if (timeline.data) return orderHistory(timeline.data.transactions);
    if (timeline.isPending) return [];
    return orderHistory(
      policy.transactions.map((x) => ({
        transactionId: x.transactionId,
        kind: x.kind,
        sequence: x.sequence,
        effectiveAt: x.effectiveAt,
        recordedAt: x.recordedAt,
        premiumChange: x.premium,
        reversed: false,
        ...(x.jobId ? { jobId: x.jobId } : {}),
      })),
    );
  }, [timeline.data, timeline.isPending, policy.transactions]);
  const denied = timeline.isError && problemOf(timeline.error).status === 403;

  return (
    <li>
      <h3 className={styles.groupHeading}>
        {t('file.history.termHeading', { number: term.termNumber })}
      </h3>
      {timeline.isPending ? (
        <div aria-busy="true">
          <SkeletonBlock width="70%" />
          <SkeletonBlock width="50%" />
        </div>
      ) : (
        <>
          {timeline.isError ? (
            <p className={styles.muted}>
              {denied ? t('file.history.timelineDenied') : t('file.history.timelineUnavailable')}
            </p>
          ) : null}
          {rows.length > 0 ? (
            <ol
              className={styles.timeline}
              aria-label={t('file.history.termList', { number: term.termNumber })}
            >
              {rows.map((row) => (
                <HistoryRow
                  key={row.transactionId}
                  row={row}
                  term={term}
                  charges={chargesOf(policy.charges, row.transactionId)}
                />
              ))}
            </ol>
          ) : (
            <p className={styles.muted}>{t('file.history.termEmpty')}</p>
          )}
        </>
      )}
    </li>
  );
}

/** «Ιστορικό συναλλαγών»: grouped by term (newest term first), newest transaction first inside a term. */
export function TransactionHistory({
  policyId,
  groups,
}: {
  policyId: string;
  groups: readonly HistoryGroupInput[];
}) {
  const { t } = useTranslation('policy');
  if (groups.length === 0) {
    return (
      <EmptyState
        kind="first-use"
        headingLevel={3}
        illustration={<></>}
        headline={t('file.history.emptyTitle')}
        description={t('file.history.emptyBody')}
      />
    );
  }
  return (
    <ol className={styles.termGroup} aria-label={t('file.history.label')}>
      {groups.map((group) => (
        <TermHistoryGroup key={group.term.termId} policyId={policyId} group={group} />
      ))}
    </ol>
  );
}
