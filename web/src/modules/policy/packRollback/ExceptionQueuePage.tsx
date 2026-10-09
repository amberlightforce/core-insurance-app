import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import type { PackRollbackExceptionView } from '../../../api/types';
import { Button, EmptyState, SegmentedControl } from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useExceptions, type ExceptionFilter } from './api';
import { useKindLabel } from './kinds';
import { ExceptionStatusPill, HashText } from './parts';
import rollbackStyles from './PackRollback.module.css';

function ExceptionRow({ item }: { item: PackRollbackExceptionView }) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const kindLabel = useKindLabel();
  return (
    <li className={rollbackStyles.row} data-testid="exception-row">
      <div className={rollbackStyles.rowHead}>
        <span className={`ds-mono ${rollbackStyles.policyNumber}`}>{item.policyNumber}</span>
        <ExceptionStatusPill status={item.status} />
        <LinkButton to={`/policies/pack-rollback/${item.exceptionId}`}>
          {t('packRollback.queue.open')}
        </LinkButton>
      </div>
      <dl className={rollbackStyles.facts}>
        <div>
          <dt>{t('packRollback.fields.transactionKind')}</dt>
          <dd>{kindLabel(item.transactionKind)}</dd>
        </div>
        <div>
          <dt>{t('packRollback.fields.productVersion')}</dt>
          <dd className="ds-mono">{item.productVersion}</dd>
        </div>
        <div>
          <dt>{t('packRollback.fields.configurationHash')}</dt>
          <dd>
            <HashText value={item.configurationHash} short />
          </dd>
        </div>
        <div>
          <dt>{t('packRollback.fields.pack')}</dt>
          <dd>
            <span className="ds-mono">
              {item.pack} {item.fromVersion}
            </span>
            <span className={rollbackStyles.arrow}> → </span>
            <span className="ds-mono">{item.toVersion}</span>
          </dd>
        </div>
        <div>
          <dt>{t('packRollback.fields.identifiedAt')}</dt>
          <dd>{fmt.dateTime(item.identifiedAt)}</dd>
        </div>
      </dl>
    </li>
  );
}

/** REQ-POL-354 (subset): transactions bound under a configuration that a pack rollback has since replaced. */
export function ExceptionQueuePage() {
  const { t } = useTranslation('policy');
  const [filter, setFilter] = useState<ExceptionFilter>('OPEN');
  const query = useExceptions(filter);
  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('packRollback.overline')}
        title={t('packRollback.queue.title')}
        description={t('packRollback.queue.subtitle')}
      />
      <Section
        title={t('packRollback.queue.section')}
        {...(query.data ? { count: query.data.items.length } : {})}
        actions={
          <SegmentedControl
            aria-label={t('packRollback.queue.filter')}
            options={[
              { id: 'OPEN', label: t('packRollback.status.OPEN') },
              { id: 'REVIEWED', label: t('packRollback.status.REVIEWED') },
            ]}
            value={filter}
            onChange={(value) => {
              setFilter(value === 'REVIEWED' ? 'REVIEWED' : 'OPEN');
            }}
          />
        }
      >
        <QueryView query={query}>
          {(page) =>
            page.items.length === 0 ? (
              <EmptyState
                kind="done"
                headingLevel={3}
                headline={t(
                  filter === 'OPEN'
                    ? 'packRollback.queue.emptyOpenTitle'
                    : 'packRollback.queue.emptyReviewedTitle',
                )}
                description={t(
                  filter === 'OPEN'
                    ? 'packRollback.queue.emptyOpenBody'
                    : 'packRollback.queue.emptyReviewedBody',
                )}
              />
            ) : (
              <ul className={rollbackStyles.list} aria-label={t('packRollback.queue.section')}>
                {page.items.map((item) => (
                  <ExceptionRow key={item.exceptionId} item={item} />
                ))}
              </ul>
            )
          }
        </QueryView>
        {query.hasNextPage ? <Button variant="secondary" isLoading={query.isFetchingNextPage} onPress={() => { void query.fetchNextPage(); }}>{t('packRollback.queue.more')}</Button> : null}
      </Section>
    </div>
  );
}
