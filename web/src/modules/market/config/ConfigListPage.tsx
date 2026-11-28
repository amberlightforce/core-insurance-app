import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import type { ConfigChangeRequestView } from '../../../api/types';
import {
  Button,
  EmptyState,
  Select,
  identifierColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import staff from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useConfigCaller } from './access';
import { useConfigChanges } from './api';
import styles from './Config.module.css';
import { changeStatuses } from './keys';
import { ConfigStatusPill } from './parts';

const ALL = 'ALL';

/** The country-layer configuration change requests (maker-checker), newest first. */
export function ConfigListPage() {
  const { t } = useTranslation('market');
  const fmt = useFormat();
  const navigate = useNavigate();
  const caller = useConfigCaller();
  const [status, setStatus] = useState(ALL);
  const query = useConfigChanges(status === ALL ? '' : status);

  const columns = useMemo<DataColumn<ConfigChangeRequestView>[]>(
    () => [
      identifierColumn<ConfigChangeRequestView>(
        'id',
        t('config.columns.id'),
        (r) => r.id.slice(-8),
        { size: 120 },
      ),
      statusColumn<ConfigChangeRequestView>(
        'status',
        t('config.columns.status'),
        (r) => r.status,
        (r) => <ConfigStatusPill status={r.status} />,
        { size: 190 },
      ),
      textColumn<ConfigChangeRequestView>('reason', t('config.columns.reason'), (r) => r.reason, {
        size: 320,
      }),
      textColumn<ConfigChangeRequestView>(
        'items',
        t('config.columns.items'),
        (r) => String(r.items.length),
        { size: 90 },
      ),
      textColumn<ConfigChangeRequestView>('maker', t('config.columns.maker'), (r) => r.createdBy, {
        size: 190,
      }),
      textColumn<ConfigChangeRequestView>(
        'created',
        t('config.columns.created'),
        (r) => fmt.dateTime(r.createdAt),
        { size: 170 },
      ),
    ],
    [t, fmt],
  );

  return (
    <div className={staff.page}>
      <PageHeader
        variant="landing"
        overline={t('config.overline')}
        title={t('config.list.title')}
        description={t('config.list.subtitle')}
        actions={
          caller.canMake ? (
            <LinkButton variant="primary" to="/admin/config/new">
              {t('config.list.new')}
            </LinkButton>
          ) : undefined
        }
      />
      <Section
        title={t('config.list.section')}
        family="brand"
        {...(query.data ? { count: query.data.items.length } : {})}
      >
        <div className={styles.filters}>
          <Select
            label={t('config.filter.status')}
            options={[
              { id: ALL, label: t('config.filter.all') },
              ...changeStatuses.map((s) => ({ id: s, label: t(`config.status.${s}`) })),
            ]}
            value={status}
            onChange={(v) => {
              if (v) setStatus(v);
            }}
          />
        </div>
        <QueryView query={query}>
          {(page) =>
            page.items.length === 0 ? (
              <EmptyState
                kind="done"
                headingLevel={3}
                headline={t('config.list.emptyTitle')}
                description={t('config.list.emptyBody')}
              />
            ) : (
              <SimpleTable<ConfigChangeRequestView>
                aria-label={t('config.list.section')}
                columns={columns}
                data={page.items}
                getRowId={(r) => r.id}
                getRowLabel={(r) => r.reason}
                onOpen={(r) => {
                  void navigate(`/admin/config/${r.id}`);
                }}
              />
            )
          }
        </QueryView>
        {query.hasNextPage ? (
          <Button
            variant="secondary"
            isLoading={query.isFetchingNextPage}
            onPress={() => {
              void query.fetchNextPage();
            }}
          >
            {t('config.list.more')}
          </Button>
        ) : null}
      </Section>
    </div>
  );
}
