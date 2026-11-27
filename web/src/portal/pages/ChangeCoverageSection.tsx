import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../api/idempotency';
import { Banner, Button, Checkbox, KeyValueList, announce, cx } from '../../design-system';
import { PortalProblem, PortalSection } from '../ui/PortalUi';
import { usePortalFormat } from '../ui/hooks';
import { useCodeLabel } from '../ui/labels';
import rows from '../ui/pages.module.css';
import styles from '../ui/portal.module.css';
import {
  confirmChange,
  portalKeys,
  previewChange,
  referChange,
  type ChangePreview,
  type PortalPolicy,
} from './portalApi';

type Done = 'CONFIRMED' | 'REFERRED';

/**
 * Change of cover on one policy: choose the cover, PREVIEW what it costs (nothing is bound), acknowledge, CONFIRM.
 * The permission matrix decides per change: ALLOW lets the customer confirm, REFER (or a DENY/403) sends it to the team.
 * TODO(hardening): vehicle and driver changes need the element locators, which the portal policy read does not expose
 * (it shows plate, make and model only); today the customer can change the selected covers.
 */
export function ChangeCoverageSection({ policy }: { policy: PortalPolicy }) {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  const label = useCodeLabel();
  const client = useQueryClient();
  const previewKey = useIdempotencyKey();
  const confirmKey = useIdempotencyKey();
  const [selected, setSelected] = useState<Record<string, boolean>>(() =>
    Object.fromEntries(policy.coverages.map((c) => [c.coverageCode, c.selected])),
  );
  const [acknowledged, setAcknowledged] = useState(false);
  const [done, setDone] = useState<Done | null>(null);

  const changed = policy.coverages.some((c) => selected[c.coverageCode] !== c.selected);
  const body = {
    policyId: policy.policyId,
    instructions: [
      {
        op: 'SET_COVERAGES' as const,
        coverages: policy.coverages.map((c) => ({
          coverageCode: c.coverageCode,
          selected: selected[c.coverageCode] ?? c.selected,
        })),
      },
    ],
  };

  const preview = useMutation({
    mutationFn: () => previewChange(body, previewKey.keyFor(body)),
    onSuccess: () => {
      previewKey.release();
      setAcknowledged(false);
    },
  });
  const finish = (outcome: Done) => {
    setDone(outcome);
    announce(outcome === 'CONFIRMED' ? t('chg.confirmed') : t('chg.referred'));
    void client.invalidateQueries({ queryKey: portalKeys.all });
  };
  const confirm = useMutation({
    mutationFn: (p: ChangePreview) => confirmChange(p.changeId, confirmKey.keyFor(p.changeId)),
    onSuccess: (result) => {
      confirmKey.release();
      finish(result.outcome);
    },
  });
  const refer = useMutation({
    mutationFn: (p: ChangePreview) =>
      referChange(p.changeId, confirmKey.keyFor(`refer-${p.changeId}`)),
    onSuccess: () => {
      confirmKey.release();
      finish('REFERRED');
    },
  });

  const reset = () => {
    preview.reset();
    confirm.reset();
    refer.reset();
  };

  const offer = preview.data;
  if (done) {
    return (
      <PortalSection title={t('chg.title')} family="success">
        <Banner
          variant="success"
          live="status"
          title={done === 'CONFIRMED' ? t('chg.confirmed') : t('chg.referred')}
        >
          {done === 'CONFIRMED' ? t('chg.confirmedBody') : t('chg.referredBody')}
        </Banner>
      </PortalSection>
    );
  }

  return (
    <PortalSection title={t('chg.title')} family="teal">
      <p className={styles.muted}>{t('chg.lead')}</p>
      {policy.coverages.length === 0 ? (
        <p className={styles.muted}>{t('chg.noCoverages')}</p>
      ) : (
        <fieldset className={cx(styles.stack, rows.fieldset)} disabled={offer !== undefined}>
          <legend className={styles.muted}>{t('chg.coverages')}</legend>
          {policy.coverages.map((c) => (
            <Checkbox
              key={c.coverageCode}
              isSelected={selected[c.coverageCode] ?? c.selected}
              onChange={(value) => {
                setSelected((prev) => ({ ...prev, [c.coverageCode]: value }));
              }}
            >
              {label('coverage', c.coverageCode)}
            </Checkbox>
          ))}
        </fieldset>
      )}
      {preview.isError ? (
        <PortalProblem error={preview.error} title={t('chg.previewFailed')} />
      ) : null}
      {offer ? (
        <div className={styles.stack}>
          <KeyValueList
            aria-label={t('chg.summary')}
            items={[
              { id: 'effective', label: t('chg.effective'), value: fmt.date(offer.effectiveAt) },
              {
                id: 'premium',
                label: t('chg.premiumChange'),
                value: fmt.money(offer.premiumChange),
              },
              { id: 'tax', label: t('chg.taxChange'), value: fmt.money(offer.taxChange) },
              { id: 'total', label: t('chg.totalChange'), value: fmt.money(offer.totalChange) },
              ...(offer.additionalDue
                ? [
                    {
                      id: 'due',
                      label: t('chg.additionalDue'),
                      value: fmt.money(offer.additionalDue),
                    },
                  ]
                : []),
              ...(offer.refundDue
                ? [{ id: 'refund', label: t('chg.refundDue'), value: fmt.money(offer.refundDue) }]
                : []),
              ...(offer.validUntil
                ? [
                    {
                      id: 'valid',
                      label: t('chg.validUntil'),
                      value: fmt.dateTime(offer.validUntil),
                    },
                  ]
                : []),
            ]}
          />
          {offer.outcome === 'ALLOW' ? (
            <>
              <Checkbox isSelected={acknowledged} onChange={setAcknowledged}>
                {t('chg.acknowledge')}
              </Checkbox>
              {confirm.isError ? (
                <PortalProblem error={confirm.error} title={t('chg.confirmFailed')} />
              ) : null}
              <div className={styles.actions}>
                <Button
                  variant="primary"
                  isLoading={confirm.isPending}
                  {...(acknowledged ? {} : { disabledReason: t('chg.acknowledgeFirst') })}
                  onPress={() => {
                    confirm.mutate(offer);
                  }}
                >
                  {t('chg.confirm')}
                </Button>
                <Button variant="ghost" onPress={reset}>
                  {t('chg.discard')}
                </Button>
              </div>
            </>
          ) : (
            <>
              <Banner variant="info" live="none" title={t('chg.needsTeam')}>
                {t('chg.needsTeamBody')}
              </Banner>
              {refer.isError ? (
                <PortalProblem error={refer.error} title={t('chg.referFailed')} />
              ) : null}
              <div className={styles.actions}>
                <Button
                  variant="primary"
                  isLoading={refer.isPending}
                  onPress={() => {
                    refer.mutate(offer);
                  }}
                >
                  {t('chg.sendToTeam')}
                </Button>
                <Button variant="ghost" onPress={reset}>
                  {t('chg.discard')}
                </Button>
              </div>
            </>
          )}
        </div>
      ) : (
        <div className={styles.actions}>
          <Button
            variant="secondary"
            isLoading={preview.isPending}
            {...(changed ? {} : { disabledReason: t('chg.pickFirst') })}
            onPress={() => {
              preview.mutate();
            }}
          >
            {t('chg.preview')}
          </Button>
        </div>
      )}
    </PortalSection>
  );
}
