import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import {
  EmptyState,
  TextField,
  dateColumn,
  identifierColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { usePlanInstances, type PlanInstance } from './api';

/** Payment plans: the plan instance of every attached policy term. */
export function PlansPage() {
  const { t } = useTranslation('billing');
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const query = usePlanInstances(search.trim());

  const columns = useMemo<DataColumn<PlanInstance>[]>(
    () => [
      identifierColumn<PlanInstance>('policy', t('plans.columns.policy'), (r) => r.policyNumber),
      textColumn<PlanInstance>('term', t('plans.columns.term'), (r) => String(r.termNumber)),
      textColumn<PlanInstance>('plan', t('plans.columns.plan'), (r) => r.planCode),
      textColumn<PlanInstance>('mode', t('plans.columns.mode'), (r) => r.billMode),
      textColumn<PlanInstance>('method', t('plans.columns.method'), (r) => r.method),
      dateColumn<PlanInstance>('from', t('plans.columns.from'), (r) => r.termFrom),
      dateColumn<PlanInstance>('to', t('plans.columns.to'), (r) => r.termTo),
      textColumn<PlanInstance>('state', t('plans.columns.state'), (r) =>
        r.cancelledEffective ? t('plans.cancelled') : t('plans.active'),
      ),
    ],
    [t],
  );

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('plans.title')}
        description={t('plans.subtitle')}
        actions={
          <LinkButton variant="secondary" to="/billing">
            {t('refunds.backToBilling')}
          </LinkButton>
        }
      />
      <Section title={t('plans.listTitle')} family="teal" count={query.data?.items.length}>
        <div className={styles.grid}>
          <TextField label={t('plans.search')} value={search} onChange={setSearch} />
        </div>
        <QueryView query={query}>
          {(page) => (
            <SimpleTable<PlanInstance>
              aria-label={t('plans.listTitle')}
              columns={columns}
              data={page.items}
              getRowId={(r) => r.planInstanceId}
              onOpen={(r) => {
                void navigate(`/billing/accounts/${r.billingAccountId}`);
              }}
              emptyState={
                <EmptyState
                  kind="first-use"
                  headingLevel={3}
                  headline={t('plans.emptyTitle')}
                  description={t('plans.emptyBody')}
                />
              }
            />
          )}
        </QueryView>
      </Section>
    </div>
  );
}
