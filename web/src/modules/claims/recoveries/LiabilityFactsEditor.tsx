import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import type { ClaimView, PartySearchItem } from '../../../api/types';
import { Button, Checkbox, Select, TextField, announce } from '../../../design-system';
import { PartySearchPanel } from '../../party/PartySearchPanel';
import { PartyName } from '../../reinsurance/PartyName';
import { ProblemBanner } from '../../staff/ProblemBanner';
import styles from '../../staff/staff.module.css';
import { useUpdateLiability, type LiabilityFacts } from './api';

const faultSources = [
  'INSURED_STATEMENT',
  'JOINT_REPORT',
  'POLICE_REPORT',
  'COUNTERPARTY_INSURER',
  'HANDLER',
] as const;

/** Liability facts of a motor claim (D-SL4-17): fault %, its source, counterparty insurer, report, vehicles. */
export function LiabilityFactsEditor({ claim }: { claim: ClaimView }) {
  const { t } = useTranslation('claims');
  const { keyFor, release } = useIdempotencyKey();
  const mutation = useUpdateLiability(claim.summary.claimId);
  const current = claim.liabilityFacts ?? null;
  const [fault, setFault] = useState(current?.insuredFaultPct ?? '');
  const [source, setSource] = useState<string | null>(current?.faultSource ?? null);
  const [insurerId, setInsurerId] = useState<string | null>(
    current?.counterpartyInsurerPartyId ?? null,
  );
  const [picking, setPicking] = useState(false);
  const [joint, setJoint] = useState(current?.jointReportFlag ?? false);
  const [vehicles, setVehicles] = useState(
    current?.vehicleCount ? String(current.vehicleCount) : '',
  );
  const [greece, setGreece] = useState(current?.accidentInGreece ?? true);
  const [tried, setTried] = useState(false);
  const open = claim.summary.status === 'OPEN';

  const faultNumber = fault === '' ? null : Number(fault);
  const faultInvalid =
    faultNumber !== null &&
    (!/^\d{1,3}(\.\d{1,2})?$/.test(fault) || faultNumber < 0 || faultNumber > 100);
  const vehiclesInvalid = vehicles !== '' && !/^[1-9]\d?$/.test(vehicles);

  const submit = () => {
    setTried(true);
    if (faultInvalid || vehiclesInvalid) return;
    const facts: LiabilityFacts = {
      ...(fault !== '' ? { insuredFaultPct: fault } : {}),
      ...(source ? { faultSource: source } : {}),
      ...(insurerId ? { counterpartyInsurerPartyId: insurerId } : {}),
      jointReportFlag: joint,
      ...(vehicles !== '' ? { vehicleCount: Number(vehicles) } : {}),
      accidentInGreece: greece,
    };
    const body = { facts, expectedRecordVersion: claim.summary.recordVersion };
    mutation.mutate(
      { ...body, key: keyFor(body) },
      {
        onSuccess: () => {
          release();
          announce(t('liability.saved'));
        },
      },
    );
  };

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('liability.title')}
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <div className={styles.grid}>
        <TextField
          label={t('liability.fault')}
          helperText={t('liability.faultHelp')}
          inputMode="decimal"
          isDisabled={!open}
          value={fault}
          onChange={setFault}
          {...(tried && faultInvalid ? { errorMessage: t('liability.errors.fault') } : {})}
        />
        <Select
          label={t('liability.source')}
          isDisabled={!open}
          options={faultSources.map((s) => ({ id: s, label: t(`liability.sources.${s}`) }))}
          value={source}
          onChange={setSource}
        />
        <TextField
          label={t('liability.vehicles')}
          inputMode="numeric"
          isDisabled={!open}
          value={vehicles}
          onChange={setVehicles}
          {...(tried && vehiclesInvalid ? { errorMessage: t('liability.errors.vehicles') } : {})}
        />
      </div>
      <div className={styles.stack}>
        <p className="ds-label">{t('liability.insurer')}</p>
        {insurerId ? (
          <p>
            <PartyName partyId={insurerId} />{' '}
            {open ? (
              <Button
                variant="secondary"
                size="sm"
                onPress={() => {
                  setInsurerId(null);
                  setPicking(true);
                }}
              >
                {t('liability.changeInsurer')}
              </Button>
            ) : null}
          </p>
        ) : open ? (
          picking ? (
            <PartySearchPanel
              pickOnClick
              label={t('liability.insurerSearch')}
              onOpen={(party: PartySearchItem) => {
                setInsurerId(party.partyId);
                setPicking(false);
              }}
            />
          ) : (
            <div>
              <Button
                variant="secondary"
                size="sm"
                onPress={() => {
                  setPicking(true);
                }}
              >
                {t('liability.pickInsurer')}
              </Button>
            </div>
          )
        ) : (
          <p className={styles.muted}>—</p>
        )}
      </div>
      <div className={styles.stack}>
        <Checkbox isSelected={joint} isDisabled={!open} onChange={setJoint}>
          {t('liability.joint')}
        </Checkbox>
        <Checkbox isSelected={greece} isDisabled={!open} onChange={setGreece}>
          {t('liability.greece')}
        </Checkbox>
      </div>
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('liability.failed')} />
      ) : null}
      {open ? (
        <div className={styles.actions}>
          <Button type="submit" variant="secondary" isLoading={mutation.isPending}>
            {t('liability.save')}
          </Button>
        </div>
      ) : null}
    </form>
  );
}
