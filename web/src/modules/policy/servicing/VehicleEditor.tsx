import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import {
  CurrencyField,
  IdentifierField,
  SegmentedControl,
  Select,
  TextField,
} from '../../../design-system';
import { Section } from '../../staff/PageHeader';
import styles from '../../staff/staff.module.css';
import { fuelTypes, vehicleValueIssue, type FieldIssue, type VehicleForm } from '../../quote/state';
import { validateServicingVehicle } from './logic';

export type VehicleMode = 'edit' | 'replace';

export interface VehicleEditorProps {
  mode: VehicleMode;
  onModeChange: (mode: VehicleMode) => void;
  form: VehicleForm;
  onChange: (update: (form: VehicleForm) => VehicleForm) => void;
  /** An Own damage (or other vehicle-value rated) cover is on the policy: the value is required. */
  valueNeeded: boolean;
}

const thisYear = () => new Date().getFullYear();

/**
 * Edit the rating fields of the insured vehicle, or replace it with another one (REQ-POL-191, -195). The labels
 * and the validation are the quote wizard's (same required inputs, same plain messages). Identity fields are read
 * only while editing: another plate is a replacement.
 */
export function VehicleEditor({
  mode,
  onModeChange,
  form,
  onChange,
  valueNeeded,
}: VehicleEditorProps) {
  const { t } = useTranslation('policy');
  const q = useTranslation('quote').t;
  const [touched, setTouched] = useState<ReadonlySet<keyof VehicleForm>>(new Set());
  const issues = validateServicingVehicle(form, thisYear(), mode);
  const set = (field: keyof VehicleForm) => (value: string | null) => {
    onChange((f) => ({ ...f, [field]: value ?? '' }));
  };
  const touch = (field: keyof VehicleForm) => () => {
    setTouched((s) => new Set(s).add(field));
  };
  const issueText = (issue: FieldIssue | undefined) => (issue ? q(`issues.${issue}`) : undefined);
  const err = (field: keyof VehicleForm) =>
    touched.has(field) ? issueText(issues[field]) : undefined;
  const errorProp = (message: string | undefined) => (message ? { errorMessage: message } : {});
  const identityReadOnly = mode === 'edit';

  return (
    <div className={styles.stack}>
      <SegmentedControl
        label={t('servicing.change.vehicle.mode')}
        value={mode}
        onChange={(value) => {
          onModeChange(value as VehicleMode);
        }}
        options={[
          { id: 'edit', label: t('servicing.change.vehicle.edit') },
          { id: 'replace', label: t('servicing.change.vehicle.replace') },
        ]}
      />
      <p className={styles.muted}>
        {t(
          mode === 'edit'
            ? 'servicing.change.vehicle.editHelp'
            : 'servicing.change.vehicle.replaceHelp',
        )}
      </p>
      <Section title={q('vehicle.identity')} headingLevel={3}>
        <div className={styles.grid}>
          <IdentifierField
            kind="plate"
            label={q('vehicle.plate')}
            isRequired
            isReadOnly={identityReadOnly}
            value={form.plate}
            onChange={set('plate')}
            onBlur={touch('plate')}
            {...errorProp(err('plate'))}
          />
          <IdentifierField
            kind="vin"
            label={q('vehicle.vin')}
            isReadOnly={identityReadOnly}
            value={form.vin}
            onChange={set('vin')}
          />
          <TextField
            label={q('vehicle.make')}
            isRequired
            isReadOnly={identityReadOnly}
            value={form.make}
            onChange={set('make')}
            onBlur={touch('make')}
            {...errorProp(err('make'))}
          />
          <TextField
            label={q('vehicle.model')}
            isRequired
            isReadOnly={identityReadOnly}
            value={form.model}
            onChange={set('model')}
            onBlur={touch('model')}
            {...errorProp(err('model'))}
          />
        </div>
      </Section>
      <Section title={q('vehicle.technical')} headingLevel={3}>
        <div className={styles.grid}>
          <TextField
            label={q('vehicle.year')}
            isRequired
            inputMode="numeric"
            value={form.firstRegistrationYear}
            onChange={set('firstRegistrationYear')}
            onBlur={touch('firstRegistrationYear')}
            {...errorProp(err('firstRegistrationYear'))}
          />
          <TextField
            label={q('vehicle.engine')}
            isRequired
            inputMode="numeric"
            suffix="cc"
            value={form.engineCapacityCc}
            onChange={set('engineCapacityCc')}
            onBlur={touch('engineCapacityCc')}
            {...errorProp(err('engineCapacityCc'))}
          />
          <TextField
            label={q('vehicle.power')}
            inputMode="numeric"
            suffix="kW"
            value={form.powerKw}
            onChange={set('powerKw')}
            onBlur={touch('powerKw')}
            {...errorProp(err('powerKw'))}
          />
          <Select
            label={q('vehicle.fuel')}
            options={fuelTypes.map((f) => ({ id: f, label: q(`vehicle.fuelTypes.${f}`) }))}
            value={form.fuelType || null}
            onChange={set('fuelType')}
          />
          <CurrencyField
            label={q('vehicle.value')}
            isRequired={valueNeeded}
            value={form.value || null}
            onChange={(value) => {
              set('value')(value);
            }}
            {...errorProp(issueText(vehicleValueIssue(form.value, valueNeeded)))}
          />
          <TextField
            label={q('vehicle.garaging')}
            isRequired
            inputMode="numeric"
            value={form.garagingPostcode}
            onChange={set('garagingPostcode')}
            onBlur={touch('garagingPostcode')}
            {...errorProp(err('garagingPostcode'))}
          />
        </div>
      </Section>
    </div>
  );
}
