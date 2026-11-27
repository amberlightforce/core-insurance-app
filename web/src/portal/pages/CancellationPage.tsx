import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';

import { useIdempotencyKey } from '../../api/idempotency';
import { Banner, Button, Select, TextField, announce } from '../../design-system';
import { PortalHero, PortalPage, PortalProblem, PortalSection } from '../ui/PortalUi';
import { QueryView } from '../ui/QueryView';
import { usePortalTitle } from '../ui/hooks';
import styles from '../ui/portal.module.css';
import { getPolicy, portalKeys, requestCancellation, type CancellationReason } from './portalApi';

const reasons: CancellationReason[] = [
  'NO_LONGER_NEEDED',
  'SOLD_ASSET',
  'FOUND_ALTERNATIVE',
  'PRICE',
  'OTHER',
];
const isoDate = /^\d{4}-\d{2}-\d{2}$/;

/**
 * `/portal/policies/:id/cancel`: asks us to cancel a policy. The request opens a ticket for the team; nothing is
 * cancelled here and the page never says it is (the team confirms the date and any refund).
 */
export function CancellationPage() {
  const { t } = useTranslation('portal');
  const { id = '' } = useParams();
  usePortalTitle(t('can.title'));
  const client = useQueryClient();
  const { keyFor, release } = useIdempotencyKey();
  const query = useQuery({ queryKey: portalKeys.policy(id), queryFn: () => getPolicy(id) });
  const [reason, setReason] = useState<CancellationReason | null>(null);
  const [date, setDate] = useState('');
  const [tried, setTried] = useState(false);

  const dateError = date.trim() !== '' && !isoDate.test(date.trim()) ? t('can.badDate') : undefined;
  const mutation = useMutation({
    mutationFn: (code: CancellationReason) =>
      requestCancellation(id, code, date.trim() || undefined, keyFor({ id, code, date })),
    onSuccess: () => {
      release();
      announce(t('can.sent'));
      void client.invalidateQueries({ queryKey: portalKeys.all });
    },
  });

  return (
    <PortalPage>
      <PortalHero overline={t('can.overline')} title={t('can.title')} lead={t('can.lead')} />
      <QueryView query={query} errorTitle={t('pol.loadFailed')}>
        {(policy) =>
          mutation.isSuccess ? (
            <Banner variant="success" live="status" title={t('can.sent')}>
              {t('can.sentBody', { number: policy.policyNumber })}
            </Banner>
          ) : (
            <PortalSection
              title={t('can.formTitle', { number: policy.policyNumber })}
              family="warning"
            >
              <form
                noValidate
                className={styles.stack}
                aria-label={t('can.formTitle', { number: policy.policyNumber })}
                onSubmit={(event) => {
                  event.preventDefault();
                }}
              >
                <Select
                  label={t('can.reason')}
                  value={reason}
                  onChange={(value) => {
                    setReason(value as CancellationReason | null);
                  }}
                  options={reasons.map((r) => ({ id: r, label: t(`can.reasons.${r}`) }))}
                  {...(tried && !reason ? { errorMessage: t('can.reasonRequired') } : {})}
                />
                <TextField
                  label={t('can.date')}
                  helperText={t('can.dateHelp')}
                  value={date}
                  onChange={setDate}
                  inputMode="numeric"
                  errorMessage={dateError}
                />
                {mutation.isError ? (
                  <PortalProblem error={mutation.error} title={t('can.failed')} />
                ) : null}
                <div className={styles.actions}>
                  <Button
                    variant="primary"
                    isLoading={mutation.isPending}
                    onPress={() => {
                      setTried(true);
                      if (!reason || dateError) return;
                      mutation.mutate(reason);
                    }}
                  >
                    {t('can.submit')}
                  </Button>
                  <Link className={styles.textLink} to={`/portal/policies/${policy.policyId}`}>
                    {t('can.back')}
                  </Link>
                </div>
              </form>
            </PortalSection>
          )
        }
      </QueryView>
    </PortalPage>
  );
}
