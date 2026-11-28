import { parseDate, today } from '@internationalized/date';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import { Banner, Button, DatePicker, Dialog, KeyValueList, announce } from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useEarningStatus, useRunEarning } from './api';
import type { EarningStatus } from './types';

function Status({ status }: { status: EarningStatus }) {
  const { t } = useTranslation('finance');
  const fmt = useFormat();
  const run = status.run;
  if (!run) return <p className={styles.muted}>{t('ops.earning.noRun')}</p>;
  return (
    <KeyValueList
      aria-label={t('ops.earning.latest')}
      items={[
        { id: 'state', label: t('ops.earning.state'), value: run.state },
        { id: 'period', label: t('ops.earning.period'), value: run.period },
        { id: 'asOf', label: t('ops.earning.asOf'), value: fmt.date(run.asOf) },
        {
          id: 'started',
          label: t('ops.earning.startedAt'),
          value: run.startedAt ? fmt.dateTime(run.startedAt) : null,
        },
        {
          id: 'finished',
          label: t('ops.earning.finishedAt'),
          value: run.finishedAt ? fmt.dateTime(run.finishedAt) : null,
        },
        {
          id: 'processed',
          label: t('ops.earning.processed'),
          value: String(run.policiesProcessed),
        },
        { id: 'skipped', label: t('ops.earning.skipped'), value: String(run.termsSkipped) },
        {
          id: 'earned',
          label: t('ops.earning.earnedToDate'),
          value: run.earnedToDate?.length
            ? run.earnedToDate.map((m) => fmt.money(m)).join(', ')
            : null,
        },
        {
          id: 'posted',
          label: t('ops.earning.postedDelta'),
          value: run.postedDelta?.length
            ? run.postedDelta.map((m) => fmt.money(m)).join(', ')
            : null,
        },
      ]}
    />
  );
}

/** Earning run (fin.Earning.status, fin.Earning.run): posts earned premium as of a date. Posting is not undone by running again, so it asks first. */
export function EarningRunPage() {
  const { t } = useTranslation('finance');
  const { keyFor, release } = useIdempotencyKey();
  const status = useEarningStatus();
  const [asOf, setAsOf] = useState(today('Europe/Athens').toString());
  const [confirm, setConfirm] = useState(false);
  const run = useRunEarning(() => {
    release();
    announce(t('ops.earning.started', { date: asOf }));
  });
  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        title={t('ops.earning.title')}
        overline={t('overline')}
        description={t('ops.earning.lead')}
        actions={
          <LinkButton variant="secondary" to="/finance">
            {t('ops.back')}
          </LinkButton>
        }
      />
      <Section family="info" title={t('ops.earning.latest')}>
        <QueryView query={status}>{(data) => <Status status={data} />}</QueryView>
      </Section>
      <Section title={t('ops.earning.runTitle')} family="plum">
        <div className={styles.stack}>
          <Banner variant="warning" live="none" title={t('ops.earning.warnTitle')}>
            {t('ops.earning.warnBody')}
          </Banner>
          <DatePicker
            label={t('ops.earning.asOf')}
            isRequired
            value={parseDate(asOf)}
            onChange={(value) => {
              if (value) setAsOf(value.toString());
            }}
          />
          {run.isError ? <ProblemBanner error={run.error} title={t('ops.earning.failed')} /> : null}
          {run.isSuccess ? (
            <Banner
              variant="success"
              live="status"
              title={t('ops.earning.started', { date: asOf })}
            />
          ) : null}
          <div className={styles.actions}>
            <Button
              variant="primary"
              isLoading={run.isPending}
              onPress={() => {
                setConfirm(true);
              }}
            >
              {t('ops.earning.run')}
            </Button>
          </div>
        </div>
      </Section>
      <Dialog
        title={t('ops.earning.confirmTitle', { date: asOf })}
        tone="warning"
        isOpen={confirm}
        onOpenChange={setConfirm}
        primaryAction={{
          label: t('ops.earning.run'),
          onAction: () => {
            run.mutate({ asOf, key: keyFor({ asOf }) });
          },
        }}
      >
        <p>{t('ops.earning.confirmBody')}</p>
      </Dialog>
    </div>
  );
}
