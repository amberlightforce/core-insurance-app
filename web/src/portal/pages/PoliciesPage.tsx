import { useInfiniteQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';

import { Button, EmptyState, LoadingState, SkeletonText, StatusPill } from '../../design-system';
import { PortalHero, PortalPage, PortalProblem, PortalSection } from '../ui/PortalUi';
import { usePortalFormat, usePortalTitle } from '../ui/hooks';
import { norm, useCodeLabel } from '../ui/labels';
import rows from '../ui/pages.module.css';
import { listPolicies, portalKeys } from './portalApi';

/** `/portal/policies`: every policy the customer owns or that belongs to the household. */
export function PoliciesPage() {
  const { t } = useTranslation('portal');
  usePortalTitle(t('nav.policies'));
  const fmt = usePortalFormat();
  const label = useCodeLabel();
  const query = useInfiniteQuery({
    queryKey: portalKeys.policies,
    queryFn: ({ pageParam }) => listPolicies(pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const items = query.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <PortalPage>
      <PortalHero overline={t('pol.overline')} title={t('pol.title')} lead={t('pol.lead')} />
      {query.isPending ? (
        <LoadingState immediate>
          <SkeletonText lines={4} />
        </LoadingState>
      ) : query.isError ? (
        <PortalProblem
          error={query.error}
          title={t('pol.loadFailed')}
          onRetry={() => {
            void query.refetch();
          }}
        />
      ) : items.length === 0 ? (
        <EmptyState
          kind="first-use"
          headingLevel={2}
          headline={t('pol.empty')}
          description={t('pol.emptyBody')}
        />
      ) : (
        <PortalSection title={t('pol.listTitle', { count: items.length })} family="brand">
          <ul className={rows.rows}>
            {items.map((p) => (
              <li key={p.policyId} className={rows.row}>
                <Link
                  to={`/portal/policies/${p.policyId}`}
                  className={`${rows.rowMain} ${rows.rowLink}`}
                >
                  <span className={rows.rowTitle}>
                    {label('product', p.productCode)} ·{' '}
                    <span className={rows.mono}>{p.policyNumber}</span>
                  </span>
                  <span className={rows.rowMeta}>
                    {p.termStart && p.termEnd
                      ? t('pol.term', { start: fmt.date(p.termStart), end: fmt.date(p.termEnd) })
                      : t('pol.noTerm')}
                  </span>
                </Link>
                <div className={rows.rowSide}>
                  {p.ownership === 'HOUSEHOLD' ? (
                    <StatusPill semantic="info" text={t('pol.household')} />
                  ) : null}
                  <StatusPill
                    semantic={norm(p.status) === 'inforce' ? 'success' : 'warning'}
                    text={label('policyStatus', p.status)}
                  />
                </div>
              </li>
            ))}
          </ul>
          {query.hasNextPage ? (
            <div>
              <Button
                variant="secondary"
                isLoading={query.isFetchingNextPage}
                onPress={() => {
                  void query.fetchNextPage();
                }}
              >
                {t('states.loadMore')}
              </Button>
            </div>
          ) : null}
        </PortalSection>
      )}
    </PortalPage>
  );
}
