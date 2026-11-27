import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';

import { isApiError } from '../../api/client';
import { EmptyState, KeyValueList, StatusPill } from '../../design-system';
import { PortalHero, PortalPage, PortalSection } from '../ui/PortalUi';
import { QueryView } from '../ui/QueryView';
import { usePortalFormat, usePortalTitle } from '../ui/hooks';
import { norm, useCodeLabel } from '../ui/labels';
import rows from '../ui/pages.module.css';
import styles from '../ui/portal.module.css';
import { ChangeCoverageSection } from './ChangeCoverageSection';
import { getPolicy, portalKeys, type PortalPolicy } from './portalApi';

function Details({ policy }: { policy: PortalPolicy }) {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  const label = useCodeLabel();
  const hasTerm = policy.termStart !== undefined && policy.termEnd !== undefined;
  return (
    <PortalSection title={t('pol.cover')} family="brand">
      <KeyValueList
        aria-label={t('pol.cover')}
        items={[
          { id: 'number', label: t('pol.number'), value: policy.policyNumber, kind: 'mono' },
          { id: 'product', label: t('pol.product'), value: label('product', policy.productCode) },
          {
            id: 'status',
            label: t('pol.status'),
            value: (
              <StatusPill
                semantic={norm(policy.status) === 'inforce' ? 'success' : 'warning'}
                text={label('policyStatus', policy.status)}
              />
            ),
          },
          {
            id: 'term',
            label: t('pol.termLabel'),
            value: hasTerm
              ? t('pol.term', { start: fmt.date(policy.termStart), end: fmt.date(policy.termEnd) })
              : null,
          },
          {
            id: 'plan',
            label: t('pol.paymentPlan'),
            value: policy.paymentPlan ? label('paymentPlan', policy.paymentPlan) : null,
          },
          {
            id: 'ownership',
            label: t('pol.ownership'),
            value: policy.ownership === 'OWN' ? t('pol.own') : t('pol.household'),
          },
        ]}
      />
    </PortalSection>
  );
}

function Coverages({ policy }: { policy: PortalPolicy }) {
  const { t } = useTranslation('portal');
  const label = useCodeLabel();
  const vehicles = policy.vehicles;
  return (
    <>
      <PortalSection title={t('pol.coverages')} family="success">
        {policy.coverages.length === 0 ? (
          <p className={styles.muted}>{t('pol.noCoverages')}</p>
        ) : (
          <ul className={rows.rows}>
            {policy.coverages.map((c) => (
              <li key={c.coverageCode} className={rows.row}>
                <p className={rows.rowTitle}>{label('coverage', c.coverageCode)}</p>
                <StatusPill
                  semantic={c.selected ? 'success' : 'disabled'}
                  text={c.selected ? t('pol.included') : t('pol.notIncluded')}
                />
              </li>
            ))}
          </ul>
        )}
      </PortalSection>
      {vehicles.length > 0 ? (
        <PortalSection title={t('pol.vehicles')} family="info">
          <ul className={rows.rows}>
            {vehicles.map((v) => (
              <li key={v.plate} className={rows.row}>
                <div className={rows.rowMain}>
                  <p className={`${rows.rowTitle} ${rows.mono}`}>{v.plate}</p>
                  <p className={rows.rowMeta}>
                    {[v.make, v.model, v.firstRegistrationYear].filter(Boolean).join(' · ')}
                  </p>
                </div>
              </li>
            ))}
          </ul>
        </PortalSection>
      ) : null}
    </>
  );
}

function Premium({ policy }: { policy: PortalPolicy }) {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  const label = useCodeLabel();
  if (policy.premium.length === 0) return null;
  return (
    <PortalSection title={t('pol.premium')} family="warning">
      <ul className={rows.rows}>
        {policy.premium.map((line) => (
          <li key={`${line.category}-${line.amount.currency}`} className={rows.row}>
            <p className={rows.rowTitle}>{label('premium', line.category)}</p>
            <p className={rows.amount}>{fmt.money(line.amount)}</p>
          </li>
        ))}
      </ul>
    </PortalSection>
  );
}

/** `/portal/policies/:id`: cover, term, premium, documents and the change / renew / cancel actions. */
export function PolicyDetailPage() {
  const { t } = useTranslation('portal');
  const { id = '' } = useParams();
  const query = useQuery({ queryKey: portalKeys.policy(id), queryFn: () => getPolicy(id) });
  usePortalTitle(
    query.data ? t('pol.detailTitle', { number: query.data.policyNumber }) : t('nav.policies'),
  );

  // A policy that is not the caller's is 404, exactly like one that does not exist.
  if (query.isError && isApiError(query.error) && query.error.status === 404) {
    return (
      <PortalPage>
        <EmptyState
          kind="first-use"
          headingLevel={1}
          headline={t('pol.notFound')}
          description={t('pol.notFoundBody')}
          action={
            <Link className={styles.textLink} to="/portal/policies">
              {t('pol.backToList')}
            </Link>
          }
        />
      </PortalPage>
    );
  }

  return (
    <PortalPage>
      <QueryView query={query} errorTitle={t('pol.loadFailed')}>
        {(policy) => (
          <>
            <PortalHero
              overline={t('pol.overline')}
              title={t('pol.detailTitle', { number: policy.policyNumber })}
            />
            <PortalSection title={t('pol.actions')} family="plum">
              <div className={styles.actions}>
                {policy.renewalJobId ? (
                  <Link
                    className={styles.textLink}
                    to={`/portal/policies/${policy.policyId}/renew`}
                  >
                    {t('pol.reviewRenewal')}
                  </Link>
                ) : null}
                <Link
                  className={styles.textLink}
                  to={`/portal/documents?policy=${policy.policyId}`}
                >
                  {t('pol.documents')}
                </Link>
                <Link className={styles.textLink} to={`/portal/policies/${policy.policyId}/cancel`}>
                  {t('pol.cancel')}
                </Link>
                <Link className={styles.textLink} to="/portal/messages">
                  {t('pol.writeToUs')}
                </Link>
              </div>
            </PortalSection>
            <Details policy={policy} />
            <Coverages policy={policy} />
            <Premium policy={policy} />
            {policy.ownership === 'OWN' ? (
              <ChangeCoverageSection key={policy.policyId} policy={policy} />
            ) : null}
            <div>
              <Link className={styles.textLink} to="/portal/policies">
                {t('pol.backToList')}
              </Link>
            </div>
          </>
        )}
      </QueryView>
    </PortalPage>
  );
}
