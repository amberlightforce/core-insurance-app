import { useTranslation } from 'react-i18next';

import { Button } from '../../design-system';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import type { MartRunSummary } from './martApi';
import local from './reports.module.css';
import { RunPill } from './RunPill';

/** The newest runs of the legal entity (dat.Mart.list). */
export function RunHistory({
  runs,
  selected,
  onSelect,
}: {
  runs: MartRunSummary[];
  selected: string | null;
  onSelect: (runId: string) => void;
}) {
  const { t } = useTranslation('reports');
  const fmt = useFormat();
  if (runs.length === 0) return <p className={styles.muted}>{t('history.empty')}</p>;
  return (
    <>
      <div className={styles.scroll}>
        <table className={styles.table}>
          <caption>{t('history.title')}</caption>
          <thead>
            <tr>
              <th scope="col">{t('history.mart')}</th>
              <th scope="col">{t('run.period')}</th>
              <th scope="col">{t('history.started')}</th>
              <th scope="col">{t('history.status')}</th>
              <th scope="col">{t('figures.actions')}</th>
            </tr>
          </thead>
          <tbody>
            {runs.map((r) => (
              <tr key={r.runId} className={r.runId === selected ? local.selected : undefined}>
                <td>{t(`catalogue.marts.${r.mart}.name`, { defaultValue: r.mart })}</td>
                <td>{r.period || t('run.allPeriods')}</td>
                <td>{fmt.dateTime(r.createdAt)}</td>
                <td>
                  <RunPill run={r} />
                </td>
                <td>
                  <Button
                    size="sm"
                    variant="ghost"
                    aria-label={t('history.openRun', {
                      mart: t(`catalogue.marts.${r.mart}.name`, { defaultValue: r.mart }),
                      when: fmt.dateTime(r.createdAt),
                    })}
                    onPress={() => {
                      onSelect(r.runId);
                    }}
                  >
                    {t('catalogue.open')}
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className={styles.muted}>{t('history.note')}</p>
    </>
  );
}
