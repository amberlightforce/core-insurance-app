import { ChevronDown, ChevronUp, CircleAlert } from 'lucide-react';
import { use, useId, type KeyboardEvent } from 'react';
import { I18nProvider } from 'react-aria';
import {
  Button as AriaButton,
  Group,
  Input,
  NumberField,
  NumberFieldStateContext,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { percentOfAmount } from '../../../format/money-input';
import {
  formatMoney,
  formatPercent,
  formatPerMille,
  type RegionFormat,
} from '../../../format/numbers';
import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx, defined } from '../../utils/cx';
import { AuthorityMeter } from '../AuthorityMeter';
import { useControlledValue } from '../Combobox/useControlledValue';
import { FieldChromeLabel, FieldMessageList, useFieldMessages } from '../FieldChrome';
import fieldStyles from '../FieldChrome/FieldChrome.module.css';
import { parsePercentText, stepValue } from './percent';
import styles from './PercentField.module.css';

export interface PercentFieldProps {
  label: string;
  /** Percentage points (15 = 15 %), or per mille points with `perMille`; null when empty. */
  value?: number | null;
  defaultValue?: number | null;
  onChange?: (value: number | null) => void;
  /** `deviation`: signed discount/loading against the technical premium, with delta and authority meter. */
  variant?: 'standard' | 'deviation';
  /** Decimals: 2 by default, 4 for rating (RAT). */
  fractionDigits?: number;
  /** ‰ instead of %. */
  perMille?: boolean;
  /** Arrow-key step (default 1; 0,5 for deviations). Shift multiplies by 10. */
  step?: number;
  minValue?: number;
  maxValue?: number;
  /** Deviation: the technical premium (exact decimal string) for the delta «−7,50 % = −31,20 €». */
  baseAmount?: string;
  /** Deviation: the user's limit in percentage points (shows «Όριο σας ±10 %» and a meter). */
  authorityLimit?: number;
  region?: RegionFormat;
  description?: string;
  errorMessage?: string;
  isRequired?: boolean;
  isReadOnly?: boolean;
  isDisabled?: boolean;
  disabledReason?: string;
  placeholder?: string;
  name?: string;
  className?: string;
}

interface PercentInputProps {
  region: RegionFormat;
  step: number;
  fractionDigits: number;
  minValue: number | undefined;
  maxValue: number | undefined;
  softDisabled: boolean;
  readOnlyLike: boolean;
  isDisabled: boolean;
  isInvalid: boolean;
  isReadOnly: boolean;
  unit: string;
  placeholder: string | undefined;
}

/** Input and steppers inside the NumberField; arrows step by `step`, Shift or PgUp/PgDn by 10 steps. */
function PercentInput(props: PercentInputProps) {
  const { t } = useTranslation('ds');
  const state = use(NumberFieldStateContext);
  const stepBy = (multiple: number) => {
    if (!state || props.readOnlyLike || props.isDisabled) return;
    const typed = parsePercentText(state.inputValue, props.region);
    const current = Number.isNaN(typed)
      ? Number.isNaN(state.numberValue)
        ? 0
        : state.numberValue
      : typed;
    state.setNumberValue(
      stepValue(
        current,
        props.step * multiple,
        props.fractionDigits,
        props.minValue,
        props.maxValue,
      ),
    );
  };
  const handleKeyDownCapture = (e: KeyboardEvent<HTMLDivElement>) => {
    const tenfold = e.shiftKey ? 10 : 1;
    let multiple = 0;
    if (e.key === 'ArrowUp') multiple = tenfold;
    else if (e.key === 'ArrowDown') multiple = -tenfold;
    else if (e.key === 'PageUp') multiple = 10;
    else if (e.key === 'PageDown') multiple = -10;
    if (multiple === 0) return;
    // Our step replaces React Aria's (which would snap values to the step grid).
    e.preventDefault();
    e.stopPropagation();
    stepBy(multiple);
  };
  return (
    <Group
      className={cx(fieldStyles.frame, styles.frame)}
      isInvalid={props.isInvalid}
      isDisabled={props.isDisabled}
      data-readonly={props.isReadOnly || undefined}
      data-soft-disabled={props.softDisabled || undefined}
      onKeyDownCapture={handleKeyDownCapture}
    >
      {props.isInvalid ? (
        <span className={fieldStyles.statusIcon}>
          <Icon icon={CircleAlert} size={16} />
        </span>
      ) : null}
      <Input
        className={cx(fieldStyles.input, styles.input)}
        {...(props.softDisabled ? { 'aria-disabled': true } : {})}
        {...defined({ placeholder: props.placeholder })}
      />
      <span className={styles.unit} aria-hidden="true">
        {props.unit}
      </span>
      <span className={cx(styles.steppers, fieldStyles.hideWhenReadOnly)}>
        <AriaButton
          slot={null}
          className={cx(styles.stepper)}
          aria-label={t('percentField.increase')}
          excludeFromTabOrder
          onPress={() => {
            stepBy(1);
          }}
        >
          <Icon icon={ChevronUp} size={12} />
        </AriaButton>
        <AriaButton
          slot={null}
          className={cx(styles.stepper)}
          aria-label={t('percentField.decrease')}
          excludeFromTabOrder
          onPress={() => {
            stepBy(-1);
          }}
        >
          <Icon icon={ChevronDown} size={12} />
        </AriaButton>
      </span>
    </Group>
  );
}

/**
 * Percentage input (Part 2 §4.6): React Aria `NumberField` in the region format. el «15 %» (the unit is a
 * visible, screen-reader-hidden suffix; screen readers hear «τοις εκατό»), 2 decimals (4 for RAT), ‰ variant.
 * Step 1 (0,5 for deviations), Shift ×10; values are never snapped to the step. Deviation variant: signed,
 * with the delta against the technical premium («−7,50 % = −31,20 €») and an authority meter
 * («Όριο σας ±10 %»). Steppers are hidden in compact density and shown on hover in comfortable.
 */
export function PercentField({
  label,
  value: valueProp,
  defaultValue = null,
  onChange,
  variant = 'standard',
  fractionDigits = 2,
  perMille = false,
  step: stepProp,
  minValue: minProp,
  maxValue: maxProp,
  baseAmount,
  authorityLimit,
  region: regionProp,
  description,
  errorMessage,
  isRequired = false,
  isReadOnly = false,
  isDisabled = false,
  disabledReason,
  placeholder,
  name,
  className,
}: PercentFieldProps) {
  const { t } = useTranslation('ds');
  const preferredRegion = useRegionFormat();
  const region = regionProp ?? preferredRegion;
  const [value, setValue] = useControlledValue<number | null>(valueProp, defaultValue, onChange);
  const deviation = variant === 'deviation';
  const step = stepProp ?? (deviation ? 0.5 : 1);
  const minValue = minProp ?? (deviation ? -100 : 0);
  const maxValue = maxProp ?? (deviation ? undefined : perMille ? 1000 : 100);
  const softDisabled = disabledReason !== undefined && disabledReason !== '';
  const readOnlyLike = isReadOnly || softDisabled;
  const unitId = useId();
  const deltaId = useId();

  const formatUnit = perMille ? formatPerMille : formatPercent;
  const delta =
    deviation && baseAmount !== undefined && value !== null && value !== 0
      ? t('percentField.delta', {
          percent: formatUnit(value, { region, fractionDigits, signDisplay: 'exceptZero' }),
          amount: formatMoney(percentOfAmount(baseAmount, perMille ? value / 10 : value), {
            region,
            signDisplay: 'exceptZero',
          }),
        })
      : undefined;
  const overLimit =
    deviation && authorityLimit !== undefined && value !== null && Math.abs(value) > authorityLimit;

  const messages = useFieldMessages({
    description,
    errorMessage,
    warning: overLimit ? t('percentField.overAuthority') : undefined,
    disabledReason,
    extraDescribedBy: [delta === undefined ? null : deltaId, unitId].filter(Boolean).join(' '),
  });
  const isInvalid = messages.showError;

  return (
    <div className={cx(fieldStyles.field, styles.percent, className)}>
      <I18nProvider locale={region}>
        <NumberField
          className={cx(fieldStyles.field)}
          value={value ?? Number.NaN}
          onChange={(next) => {
            setValue(Number.isNaN(next) ? null : next);
          }}
          formatOptions={{
            minimumFractionDigits: 0,
            maximumFractionDigits: fractionDigits,
            signDisplay: deviation ? 'exceptZero' : 'auto',
          }}
          isRequired={isRequired}
          isInvalid={isInvalid}
          isDisabled={isDisabled}
          isReadOnly={readOnlyLike}
          validationBehavior="aria"
          {...defined({ minValue, maxValue, name, 'aria-describedby': messages.describedBy })}
        >
          <FieldChromeLabel isRequired={isRequired}>{label}</FieldChromeLabel>
          <PercentInput
            region={region}
            step={step}
            fractionDigits={fractionDigits}
            minValue={minValue}
            maxValue={maxValue}
            softDisabled={softDisabled}
            readOnlyLike={readOnlyLike}
            isDisabled={isDisabled}
            isInvalid={isInvalid}
            isReadOnly={isReadOnly}
            unit={perMille ? '‰' : '%'}
            placeholder={placeholder}
          />
        </NumberField>
      </I18nProvider>
      <span id={unitId} hidden>
        {perMille ? t('percentField.unitPerMille') : t('percentField.unit')}
      </span>
      <FieldMessageList
        messages={messages}
        description={description}
        errorMessage={errorMessage}
        warning={t('percentField.overAuthority')}
        disabledReason={disabledReason}
      />
      {delta !== undefined ? (
        <div id={deltaId} className={styles.delta}>
          {delta}
        </div>
      ) : null}
      {deviation && authorityLimit !== undefined ? (
        <AuthorityMeter
          label={t('percentField.authority', {
            limit: formatUnit(authorityLimit, { region, fractionDigits: 0 }),
          })}
          value={value ?? 0}
          limit={authorityLimit}
          format="percent"
          size="inline"
          hideValue
        />
      ) : null}
    </div>
  );
}
