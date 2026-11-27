import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { Button, TextField, type StatusFamily } from '../../design-system';
import { ProblemBanner } from '../staff/ProblemBanner';
import { Section } from '../staff/PageHeader';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { martCodes, useRunMart, type MartCode, type MartRunSummary } from './martApi';
import local from './reports.module.css';
import { RunPill } from './RunPill';

const families: Record<MartCode, StatusFamily> = {
  PORTFOLIO_PREMIUM: 'teal',
  CLAIMS_EXPERIENCE: 'warning',
  RI_CESSION_SUMMARY: 'plum',
};

/** One card per mart (BO-DAT-03 registry): name, last run, status and the run button. */
export function MartCatalogue({
  runs,
  onStarted,
  onSelect,
}: {
  runs: MartRunSummary[];
  onStarted: (runId: string) => void;
  onSelect: (runId: string) => void;
}) {
  const { t } = useTranslation('reports');
  const fmt = useFormat();
  const [period, setPeriod] = useState('');
  const [running, setRunning] = useState<MartCode | null>(null);
  const run = useRunMart(onStarted);
  return (
    <div className={styles.stack}>
      <TextField
        label={t('catalogue.period')}
        helperText={t('catalogue.periodHelp')}
        value={period}
        onChange={setPeriod}
        maxLength={20}
      />
      {run.isError ? <ProblemBanner error={run.error} title={t('catalogue.failed')} /> : null}
      <div className={local.catalogue}>
        {martCodes.map((code) => {
          const last = runs.find((r) => r.mart === code);
          return (
            <Section
              key={code}
              title={t(`catalogue.marts.${code}.name`)}
              family={families[code]}
              headingLevel={3}
              meta={t('catalogue.notRegulatory')}
            >
              <div className={local.cardBody}>
                <p className={styles.muted}>{t(`catalogue.marts.${code}.description`)}</p>
                <div className={local.cardMeta}>
                  {last ? (
                    <>
                      <RunPill run={last} />
                      <span>{t('catalogue.lastRun', { when: fmt.dateTime(last.createdAt) })}</span>
                    </>
                  ) : (
                    <span>{t('catalogue.neverRun')}</span>
                  )}
                </div>
                <div className={styles.actions}>
                  <Button
                    variant="primary"
                    isLoading={run.isPending && running === code}
                    isDisabled={run.isPending}
                    onPress={() => {
                      setRunning(code);
                      run.mutate({
                        mart: code,
                        ...(period.trim() ? { period: period.trim() } : {}),
                      });
                    }}
                  >
                    {t('catalogue.run')}
                  </Button>
                  {last ? (
                    <Button
                      variant="secondary"
                      onPress={() => {
                        onSelect(last.runId);
                      }}
                    >
                      {t('catalogue.open')}
                    </Button>
                  ) : null}
                </div>
              </div>
            </Section>
          );
        })}
      </div>
    </div>
  );
}
