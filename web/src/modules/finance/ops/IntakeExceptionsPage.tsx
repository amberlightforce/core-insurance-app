import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import {
  Button,
  Dialog,
  EmptyState,
  KeyValueList,
  TextField,
  announce,
  dateColumn,
  identifierColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useIntakeAction, useIntakeExceptions, type IntakeAction } from './api';
import type { IntakeException } from './types';

function Selected({ item, onDone }: { item: IntakeException; onDone: () => void }) {
  const { t } = useTranslation('finance');
  const fmt = useFormat();
  const { keyFor, release } = useIdempotencyKey();
  const [confirmReject, setConfirmReject] = useState(false);
  const [last, setLast] = useState<IntakeAction | null>(null);
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  const mutation = useIntakeAction((response) => {
    release();
    const outcome = response.outcomes[0]?.outcome;
    announce(
      outcome === 'STILL_SUSPENDED' || outcome === 'NO_OP'
        ? t(`ops.intake.done.${outcome}`)
        : t(`ops.intake.done.${last ?? 'rerun'}`),
    );
    onDone();
  });
  const missing = reason.trim() === '';
  const run = (action: IntakeAction) => {
    setLast(action);
    const itemIds = [item.eventId];
    const trimmed = reason.trim();
    mutation.mutate({
      action,
      itemIds,
      ...(trimmed ? { reason: trimmed } : {}),
      key: keyFor({ action, itemIds, reason: trimmed }),
    });
  };
  // markDuplicate and reject close the event for good: the server requires (and audits) a reason.
  const close = (action: 'mark-duplicate' | 'reject') => {
    setTried(true);
    if (missing) return;
    if (action === 'reject') setConfirmReject(true);
    else run(action);
  };
  return (
    <div className={styles.stack}>
      <KeyValueList
        aria-label={t('ops.intake.selected')}
        items={[
          {
            id: 'reason',
            label: t('ops.intake.reason'),
            value: t(`ops.intake.reasons.${item.reason}`, { defaultValue: item.reason }),
          },
          { id: 'detail', label: t('ops.intake.detail'), value: item.detail ?? null },
          { id: 'event', label: t('ops.intake.event'), value: item.eventType, kind: 'mono' },
          {
            id: 'aggregate',
            label: t('ops.intake.aggregate'),
            value: `${item.aggregateType} ${item.aggregateId}`,
            kind: 'mono',
          },
          { id: 'attempts', label: t('ops.intake.attempts'), value: String(item.attempts) },
          {
            id: 'received',
            label: t('ops.intake.receivedAt'),
            value: fmt.dateTime(item.receivedAt),
          },
        ]}
      />
      <TextField
        label={t('ops.intake.actionReason')}
        helperText={t('ops.intake.actionReasonHelp')}
        multiline
        maxLength={1000}
        value={reason}
        onChange={setReason}
        errorMessage={tried && missing ? t('ops.intake.actionReasonRequired') : undefined}
      />
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('ops.intake.failed')} />
      ) : null}
      <div className={styles.actions}>
        <Button
          variant="primary"
          isLoading={mutation.isPending && last === 'rerun'}
          isDisabled={mutation.isPending}
          onPress={() => {
            run('rerun');
          }}
        >
          {t('ops.intake.rerun')}
        </Button>
        <Button
          variant="secondary"
          isLoading={mutation.isPending && last === 'mark-duplicate'}
          isDisabled={mutation.isPending}
          onPress={() => {
            close('mark-duplicate');
          }}
        >
          {t('ops.intake.markDuplicate')}
        </Button>
        <Button
          variant="danger"
          isDisabled={mutation.isPending}
          onPress={() => {
            close('reject');
          }}
        >
          {t('ops.intake.reject')}
        </Button>
      </div>
      <Dialog
        title={t('ops.intake.rejectTitle')}
        tone="danger"
        isOpen={confirmReject}
        onOpenChange={setConfirmReject}
        primaryAction={{
          label: t('ops.intake.reject'),
          variant: 'danger',
          onAction: () => {
            run('reject');
          },
        }}
      >
        <p>{t('ops.intake.rejectBody')}</p>
      </Dialog>
    </div>
  );
}

/** Intake exception queue (fin.IntakeException.list): suspended business events; rerun after a fix, or mark duplicate / reject. */
export function IntakeExceptionsPage() {
  const { t } = useTranslation('finance');
  const query = useIntakeExceptions();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const selectedItem = query.data?.items.find((e) => e.eventId === selectedId);

  const columns: DataColumn<IntakeException>[] = [
    textColumn<IntakeException>(
      'reason',
      t('ops.intake.reason'),
      (e) => t(`ops.intake.reasons.${e.reason}`, { defaultValue: e.reason }),
      { size: 240 },
    ),
    textColumn<IntakeException>('module', t('ops.intake.module'), (e) => e.sourceModule, {
      size: 90,
    }),
    identifierColumn<IntakeException>('event', t('ops.intake.event'), (e) => e.eventType, {
      size: 240,
    }),
    textColumn<IntakeException>('attempts', t('ops.intake.attempts'), (e) => String(e.attempts), {
      size: 90,
    }),
    dateColumn<IntakeException>('received', t('ops.intake.receivedAt'), (e) => e.receivedAt),
  ];

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        title={t('ops.intake.title')}
        overline={t('overline')}
        description={t('ops.intake.lead')}
        actions={
          <LinkButton variant="secondary" to="/finance">
            {t('ops.back')}
          </LinkButton>
        }
      />
      <Section title={t('ops.intake.queue')}>
        <QueryView query={query}>
          {(page) =>
            page.items.length === 0 ? (
              <EmptyState
                kind="first-use"
                illustration={<></>}
                headingLevel={3}
                headline={t('ops.intake.emptyTitle')}
                description={t('ops.intake.emptyBody')}
              />
            ) : (
              <SimpleTable<IntakeException>
                aria-label={t('ops.intake.queue')}
                columns={columns}
                data={page.items}
                getRowId={(e) => e.eventId}
                getRowLabel={(e) => `${e.eventType} ${e.reason}`}
                onOpen={(e) => {
                  setSelectedId(e.eventId);
                }}
              />
            )
          }
        </QueryView>
      </Section>
      {selectedItem ? (
        <Section title={t('ops.intake.selected')} family="warning">
          <Selected
            item={selectedItem}
            onDone={() => {
              setSelectedId(null);
            }}
          />
        </Section>
      ) : null}
    </div>
  );
}
