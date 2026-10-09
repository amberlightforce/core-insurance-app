import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router';

import {
  Button,
  EmptyState,
  Select,
  identifierColumn,
  percentColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import staff from '../staff/staff.module.css';
import { contractStatuses, isContractStatus, useContractList, type ContractListItem } from './api';
import { ContractStatusPill } from './ContractStatusPill';
import styles from './Reinsurance.module.css';
import { useRiFormat } from './useRiFormat';

const all = 'ALL';

/** The treaty registry («Συμβάσεις αντασφάλισης», REQ-RI-001): number, year, type, period, placed %, status. */
export function ReinsuranceHomePage() {
  const { t } = useTranslation('reinsurance');
  const fmt = useRiFormat();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const raw = params.get('status');
  const status = isContractStatus(raw) ? raw : null;
  const { query, items } = useContractList(status);

  const statusOptions = useMemo(
    () => [
      { id: all, label: t('registry.allStatuses') },
      ...contractStatuses.map((s) => ({ id: s, label: t(`status.${s}`) })),
    ],
    [t],
  );

  const columns = useMemo<DataColumn<ContractListItem>[]>(
    () => [
      identifierColumn<ContractListItem>(
        'number',
        t('registry.columns.number'),
        (c) => c.contractNumber ?? t('registry.noNumber'),
        { size: 170 },
      ),
      textColumn<ContractListItem>('year', t('registry.columns.year'), (c) => String(c.contractYear), { size: 90 }),
      textColumn<ContractListItem>('type', t('registry.columns.type'), (c) => t(`type.${c.contractType}`), { size: 200 }),
      textColumn<ContractListItem>(
        'period',
        t('registry.columns.period'),
        (c) => `${fmt.date(c.period.from)} – ${c.period.to ? fmt.date(c.period.to) : t('contract.openEnded')}`,
        { size: 230 },
      ),
      textColumn<ContractListItem>('currency', t('registry.columns.currency'), (c) => c.currency, { size: 100 }),
      percentColumn<ContractListItem>('placed', t('registry.columns.placed'), (c) => c.placedPct, { size: 120 }),
      statusColumn<ContractListItem>(
        'status',
        t('registry.columns.status'),
        (c) => c.status,
        (c) => <ContractStatusPill status={c.status} />,
        { size: 190 },
      ),
    ],
    [t, fmt],
  );

  return (
    <div className={staff.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('registry.title')}
        description={t('registry.lead')}
      />
      <Section title={t('registry.section')} count={items.length} meta={t('registry.meta')}>
        <div className={styles.filters}>
          <Select
            label={t('registry.filterStatus')}
            options={statusOptions}
            value={status ?? all}
            onChange={(value) => {
              const next = new URLSearchParams(params);
              if (!value || value === all) next.delete('status');
              else next.set('status', value);
              setParams(next, { replace: true });
            }}
          />
        </div>
        <QueryView query={query}>
          {() => (
            <>
              <SimpleTable<ContractListItem>
                aria-label={t('registry.section')}
                columns={columns}
                data={items}
                getRowId={(c) => c.contractId}
                getRowLabel={(c) => c.contractNumber ?? t('registry.noNumber')}
                onOpen={(c) => {
                  void navigate(`/reinsurance/contracts/${c.contractId}`);
                }}
                emptyState={
                  <EmptyState
                    kind={status ? 'no-results' : 'first-use'}
                    headingLevel={3}
                    illustration={<></>}
                    headline={status ? t('registry.noResultsTitle') : t('registry.emptyTitle')}
                    description={status ? t('registry.noResultsBody') : t('registry.emptyBody')}
                  />
                }
              />
              {query.hasNextPage ? (
                <div>
                  <Button
                    variant="secondary"
                    isLoading={query.isFetchingNextPage}
                    onPress={() => {
                      void query.fetchNextPage();
                    }}
                  >
                    {t('registry.loadMore')}
                  </Button>
                </div>
              ) : null}
            </>
          )}
        </QueryView>
      </Section>
    </div>
  );
}
