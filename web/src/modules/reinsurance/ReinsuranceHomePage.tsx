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
import { LinkButton } from '../staff/LinkButton';
import staff from '../staff/staff.module.css';
import { contractStatuses, isContractStatus, useContractList, type ContractListItem } from './api';
import { ContractStatusPill } from './ContractStatusPill';
import { layerSummary } from './layerSummary';
import styles from './Reinsurance.module.css';
import { useRiFormat } from './useRiFormat';
import { accountantRole, currentUser, hasRole } from './roles';

const all = 'ALL';
type RegistryRow = ContractListItem & { layerSummary: string };

/** The treaty registry («Συμβάσεις αντασφάλισης», REQ-RI-001): number, year, type, period, placed %, status. */
export function ReinsuranceHomePage() {
  const { t } = useTranslation('reinsurance');
  const fmt = useRiFormat();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const raw = params.get('status');
  const status = isContractStatus(raw) ? raw : null;
  const { query, items } = useContractList(status);
  const rows = useMemo<RegistryRow[]>(
    () =>
      items.map((item) => ({
        ...item,
        layerSummary:
          item.layers?.map((layer) => layerSummary(layer, fmt.region)).join('; ') ?? '—',
      })),
    [items, fmt.region],
  );

  const statusOptions = useMemo(
    () => [
      { id: all, label: t('registry.allStatuses') },
      ...contractStatuses.map((s) => ({ id: s, label: t(`status.${s}`) })),
    ],
    [t],
  );

  const columns = useMemo<DataColumn<RegistryRow>[]>(
    () => [
      identifierColumn<RegistryRow>(
        'number',
        t('registry.columns.number'),
        (c) => c.contractNumber ?? t('registry.noNumber'),
        { size: 170 },
      ),
      textColumn<RegistryRow>('year', t('registry.columns.year'), (c) => String(c.contractYear), {
        size: 90,
      }),
      textColumn<RegistryRow>(
        'type',
        t('registry.columns.type'),
        (c) => t(`type.${c.contractType}`),
        { size: 200 },
      ),
      textColumn<RegistryRow>(
        'period',
        t('registry.columns.period'),
        (c) =>
          `${fmt.date(c.period.from)} – ${c.period.to ? fmt.date(c.period.to) : t('contract.openEnded')}`,
        { size: 230 },
      ),
      textColumn<RegistryRow>('layers', t('registry.columns.layers'), (c) => c.layerSummary, {
        size: 240,
      }),
      textColumn<RegistryRow>('currency', t('registry.columns.currency'), (c) => c.currency, {
        size: 100,
      }),
      percentColumn<RegistryRow>('placed', t('registry.columns.placed'), (c) => c.placedPct, {
        size: 150,
      }),
      statusColumn<RegistryRow>(
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
        actions={
          hasRole(currentUser().roles, accountantRole) ||
          hasRole(currentUser().roles, 'Platform.Admin') ? (
            <LinkButton variant="primary" to="/reinsurance/contracts/new">
              {t('registry.new')}
            </LinkButton>
          ) : undefined
        }
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
              <SimpleTable<RegistryRow>
                aria-label={t('registry.section')}
                columns={columns}
                data={rows}
                getRowId={(c) => c.contractId}
                getRowLabel={(c) => c.contractNumber ?? t('registry.noNumber')}
                onOpen={(c) => {
                  void navigate(`/reinsurance/contracts/${c.contractId}`);
                }}
                emptyState={
                  status ? (
                    <EmptyState
                      kind="filtered"
                      headingLevel={3}
                      filters={[
                        { label: t('registry.filterStatus'), value: t(`status.${status}`) },
                      ]}
                      onClearFilters={() => {
                        const next = new URLSearchParams(params);
                        next.delete('status');
                        setParams(next, { replace: true });
                      }}
                    />
                  ) : (
                    <EmptyState
                      kind="first-use"
                      headingLevel={3}
                      headline={t('registry.emptyTitle')}
                      description={t('registry.emptyBody')}
                    />
                  )
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
