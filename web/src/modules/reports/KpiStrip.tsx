import { useTranslation } from 'react-i18next';

import { KpiTile } from '../../design-system';
import { QueryView } from '../staff/QueryView';
import { useKpis } from './martApi';
import styles from './reports.module.css';

const num = (v: number | string | null | undefined): number | null =>
  v === null || v === undefined ? null : Number(v);

/** GWP, claims paid, open reserve and loss ratio from dat.Kpi.get (definition v1: (paid + reserve) / GWP). */
export function KpiStrip() {
  const { t } = useTranslation('reports');
  const query = useKpis();
  return (
    <QueryView query={query}>
      {({ values }) => {
        const ratio = num(values.LOSS_RATIO?.ratio);
        return (
          <section className={styles.kpis} aria-label={t('kpi.title')}>
            <KpiTile
              label={t('kpi.gwp')}
              value={num(values.GWP?.amount)}
              format="money"
              currency={values.GWP?.currency ?? 'EUR'}
            />
            <KpiTile
              label={t('kpi.claimsPaid')}
              value={num(values.CLAIMS_PAID?.amount)}
              format="money"
              currency={values.CLAIMS_PAID?.currency ?? 'EUR'}
              higherIsBetter={false}
            />
            <KpiTile
              label={t('kpi.openReserve')}
              value={num(values.OPEN_RESERVE?.amount)}
              format="money"
              currency={values.OPEN_RESERVE?.currency ?? 'EUR'}
              higherIsBetter={false}
            />
            <KpiTile
              label={t('kpi.lossRatio')}
              value={ratio === null ? null : ratio * 100}
              format="percent"
              higherIsBetter={false}
            />
          </section>
        );
      }}
    </QueryView>
  );
}
