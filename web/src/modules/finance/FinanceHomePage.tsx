import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { hasAnyRole, financeRoles } from '../../auth/roles';
import { readSession } from '../../dev-auth/devAuth';
import { Button, EmptyState, TextField } from '../../design-system';
import { NavList } from '../staff/NavList';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import styles from '../staff/staff.module.css';
import { usePeriods } from './ops/api';
import { PeriodStatePill } from './ops/PeriodClosePage';

const screens = [
  ['trialBalance', '/finance/trial-balance'],
  ['manualJournals', '/finance/manual-journals'],
  ['intake', '/finance/intake-exceptions'],
  ['close', '/finance/period-close'],
  ['earning', '/finance/earning'],
] as const;

/** The latest accounting periods and their state: what the finance team is working in. */
function PeriodsSection() {
  const { t } = useTranslation('finance');
  const query = usePeriods();
  return (
    <Section title={t('ops.home.periods')} family="teal" count={query.data?.items.length}>
      <QueryView query={query}>
        {(page) =>
          page.items.length === 0 ? (
            <EmptyState
              kind="first-use"
              illustration={<></>}
              headingLevel={3}
              headline={t('ops.close.emptyTitle')}
              description={t('ops.close.emptyBody')}
            />
          ) : (
            <ul className={styles.linkList}>
              {page.items.slice(0, 6).map((p) => (
                <li key={p.period} className={styles.row}>
                  <span className="ds-mono">{p.period}</span>
                  <PeriodStatePill state={p.state} />
                </li>
              ))}
            </ul>
          )
        }
      </QueryView>
    </Section>
  );
}

/** Finance landing: the operations screens (finance roles), the latest periods, and journals by policy. */
export function FinanceHomePage() {
  const { t } = useTranslation('finance');
  const navigate = useNavigate();
  const [number, setNumber] = useState('');
  const [tried, setTried] = useState(false);
  const valid = /^[A-Za-z0-9][A-Za-z0-9-]{2,63}$/.test(number.trim());
  const ops = hasAnyRole(readSession()?.user.roles ?? [], financeRoles);
  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        title={t('home.title')}
        overline={t('overline')}
        description={t('home.lead')}
      />
      {ops ? (
        <div className={styles.grid}>
          <Section title={t('ops.home.screens')} family="brand">
            <NavList
              aria-label={t('ops.home.screens')}
              items={screens.map(([key, to]) => ({ id: key, label: t(`ops.home.${key}`), to }))}
            />
          </Section>
          <PeriodsSection />
        </div>
      ) : null}
      <Section title={t('home.byPolicy')} family="info">
        <form
          noValidate
          className={styles.row}
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (valid)
              void navigate(`/finance/journals/policy/${encodeURIComponent(number.trim())}`);
          }}
        >
          <TextField
            label={t('home.policyNumber')}
            helperText={t('home.help')}
            mono
            value={number}
            onChange={setNumber}
            errorMessage={tried && !valid ? t('home.invalid') : undefined}
          />
          <Button type="submit" variant="primary">
            {t('home.open')}
          </Button>
        </form>
      </Section>
    </div>
  );
}
