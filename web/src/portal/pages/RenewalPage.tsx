import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';

import { useIdempotencyKey } from '../../api/idempotency';
import { Banner, Button, Checkbox, KeyValueList, Select, announce } from '../../design-system';
import { PortalHero, PortalPage, PortalProblem, PortalSection } from '../ui/PortalUi';
import { QueryView } from '../ui/QueryView';
import { usePortalFormat, usePortalTitle } from '../ui/hooks';
import { useCodeLabel } from '../ui/labels';
import styles from '../ui/portal.module.css';
import {
  acceptRenewal,
  declineRenewal,
  getPolicy,
  portalKeys,
  type PortalPolicy,
  type RenewalDeclineReason,
} from './portalApi';

const reasons: RenewalDeclineReason[] = ['PRICE', 'FOUND_ALTERNATIVE', 'NO_LONGER_NEEDED', 'OTHER'];
type Outcome =
  { kind: 'ACCEPTED'; term?: number | undefined } | { kind: 'DECLINED' } | { kind: 'REFERRED' };

function RenewalActions({ policy, jobId }: { policy: PortalPolicy; jobId: string }) {
  const { t } = useTranslation('portal');
  const client = useQueryClient();
  const acceptKey = useIdempotencyKey();
  const declineKey = useIdempotencyKey();
  const [acknowledged, setAcknowledged] = useState(false);
  const [reason, setReason] = useState<RenewalDeclineReason | null>(null);
  const [outcome, setOutcome] = useState<Outcome | null>(null);

  const done = (next: Outcome) => {
    setOutcome(next);
    announce(t(`ren.done.${next.kind}`));
    void client.invalidateQueries({ queryKey: portalKeys.all });
  };
  const accept = useMutation({
    mutationFn: () => acceptRenewal(policy.policyId, jobId, acceptKey.keyFor(jobId)),
    onSuccess: (r) => {
      acceptKey.release();
      done(
        r.outcome === 'ACCEPTED'
          ? { kind: 'ACCEPTED', term: r.newTermNumber }
          : { kind: 'REFERRED' },
      );
    },
  });
  const decline = useMutation({
    mutationFn: (code: RenewalDeclineReason) =>
      declineRenewal(policy.policyId, jobId, code, declineKey.keyFor(code)),
    onSuccess: (r) => {
      declineKey.release();
      done(r.outcome === 'DECLINED' ? { kind: 'DECLINED' } : { kind: 'REFERRED' });
    },
  });

  if (outcome) {
    return (
      <Banner variant="success" live="status" title={t(`ren.done.${outcome.kind}`)}>
        {outcome.kind === 'ACCEPTED' && outcome.term
          ? t('ren.doneBody.ACCEPTED', { term: outcome.term })
          : t(`ren.doneBody.${outcome.kind}`)}
      </Banner>
    );
  }
  return (
    <>
      <PortalSection title={t('ren.acceptTitle')} family="success">
        <p className={styles.muted}>{t('ren.acceptBody')}</p>
        <Checkbox isSelected={acknowledged} onChange={setAcknowledged}>
          {t('ren.acknowledge')}
        </Checkbox>
        {accept.isError ? (
          <PortalProblem error={accept.error} title={t('ren.acceptFailed')} />
        ) : null}
        <div className={styles.actions}>
          <Button
            variant="primary"
            isLoading={accept.isPending}
            {...(acknowledged ? {} : { disabledReason: t('ren.acknowledgeFirst') })}
            onPress={() => {
              accept.mutate();
            }}
          >
            {t('ren.accept')}
          </Button>
        </div>
      </PortalSection>
      <PortalSection title={t('ren.declineTitle')} family="warning">
        <p className={styles.muted}>{t('ren.declineBody')}</p>
        <Select
          label={t('ren.reason')}
          value={reason}
          onChange={(value) => {
            setReason(value as RenewalDeclineReason | null);
          }}
          options={reasons.map((r) => ({ id: r, label: t(`ren.reasons.${r}`) }))}
        />
        {decline.isError ? (
          <PortalProblem error={decline.error} title={t('ren.declineFailed')} />
        ) : null}
        <div className={styles.actions}>
          <Button
            variant="secondary"
            isLoading={decline.isPending}
            {...(reason ? {} : { disabledReason: t('ren.pickReason') })}
            onPress={() => {
              if (reason) decline.mutate(reason);
            }}
          >
            {t('ren.decline')}
          </Button>
        </div>
      </PortalSection>
    </>
  );
}

/**
 * `/portal/policies/:id/renew`: accept or decline the offered renewal. The offer job id comes from the policy read
 * (`renewalJobId`); without one there is no offer to act on yet and the page says so instead of inventing one.
 */
export function RenewalPage() {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  const label = useCodeLabel();
  const { id = '' } = useParams();
  usePortalTitle(t('ren.title'));
  const query = useQuery({ queryKey: portalKeys.policy(id), queryFn: () => getPolicy(id) });

  return (
    <PortalPage>
      <PortalHero overline={t('ren.overline')} title={t('ren.title')} lead={t('ren.lead')} />
      <QueryView query={query} errorTitle={t('pol.loadFailed')}>
        {(policy) => (
          <>
            <PortalSection title={t('ren.policy')} family="brand">
              <KeyValueList
                aria-label={t('ren.policy')}
                items={[
                  { id: 'n', label: t('pol.number'), value: policy.policyNumber, kind: 'mono' },
                  { id: 'p', label: t('pol.product'), value: label('product', policy.productCode) },
                  { id: 'e', label: t('ren.expires'), value: fmt.date(policy.termEnd) },
                ]}
              />
            </PortalSection>
            {policy.renewalJobId ? (
              <RenewalActions
                key={policy.renewalJobId}
                policy={policy}
                jobId={policy.renewalJobId}
              />
            ) : (
              <Banner variant="info" live="none" title={t('ren.noOffer')}>
                {t('ren.noOfferBody')}
              </Banner>
            )}
            <div>
              <Link className={styles.textLink} to={`/portal/policies/${policy.policyId}`}>
                {t('ren.backToPolicy')}
              </Link>
            </div>
          </>
        )}
      </QueryView>
    </PortalPage>
  );
}
