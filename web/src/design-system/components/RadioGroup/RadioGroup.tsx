import { Check } from 'lucide-react';
import { useId, type ReactNode, type Ref } from 'react';
import { RadioButton, RadioField, RadioGroup as AriaRadioGroup, Text } from 'react-aria-components';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { hasText, joinIds, messageIds, useFieldIds } from '../TextField/fieldHooks';
import { FieldLabel, FieldMessages } from '../TextField/FormField';
import styles from './RadioGroup.module.css';

export interface RadioGroupProps {
  label: string;
  children: ReactNode;
  id?: string;
  name?: string;
  value?: string | null;
  defaultValue?: string | null;
  onChange?: (value: string) => void;
  /** Vertical by default; horizontal only for ≤ 3 short options. */
  orientation?: 'vertical' | 'horizontal';
  isRequired?: boolean;
  isDisabled?: boolean;
  /** Read-only: only the selected option's label, as text. */
  isReadOnly?: boolean;
  help?: ReactNode;
  helperText?: string;
  errorMessage?: string | undefined;
  /** `cards` lays out ChoiceCards in a responsive row. */
  variant?: 'list' | 'cards';
  ref?: Ref<HTMLDivElement>;
}

/**
 * Radio group (Part 2 §4.9) on React Aria `RadioGroup`: Tab lands on the selected option, arrows move **and
 * select**. Holds `Radio` or `ChoiceCard` children.
 */
export function RadioGroup({
  label,
  children,
  id,
  name,
  value,
  defaultValue,
  onChange,
  orientation = 'vertical',
  isRequired = false,
  isDisabled = false,
  isReadOnly = false,
  help,
  helperText,
  errorMessage,
  variant = 'list',
  ref,
}: RadioGroupProps) {
  const ids = useFieldIds();
  const invalid = hasText(errorMessage);
  const describedBy = joinIds(...messageIds(ids, { errorMessage, helperText }));
  return (
    <AriaRadioGroup
      ref={ref ?? null}
      className={cx(styles.group)}
      {...(id ? { id } : {})}
      {...(name ? { name } : {})}
      {...(value !== undefined ? { value } : {})}
      {...(defaultValue !== undefined ? { defaultValue } : {})}
      {...(onChange ? { onChange } : {})}
      {...(describedBy ? { 'aria-describedby': describedBy } : {})}
      orientation={orientation}
      isRequired={isRequired}
      isDisabled={isDisabled}
      isReadOnly={isReadOnly}
      isInvalid={invalid}
      validationBehavior="aria"
    >
      <FieldLabel isRequired={isRequired} help={help} labelText={label} elementType="span">
        {label}
      </FieldLabel>
      <div className={styles.items} data-orientation={orientation} data-variant={variant}>
        {children}
      </div>
      <FieldMessages ids={ids} errorMessage={errorMessage} helperText={helperText} />
    </AriaRadioGroup>
  );
}

export interface RadioProps {
  value: string;
  children: ReactNode;
  description?: string;
  isDisabled?: boolean;
}

/** A radio: 16 px circle (20 on touch) with a 6 px dot (8 on touch); MI-05 dot scales in. */
export function Radio({ value, children, description, isDisabled }: RadioProps) {
  return (
    <RadioField className={cx(styles.field)} value={value} {...(isDisabled ? { isDisabled } : {})}>
      <RadioButton className={cx(styles.radio)}>
        <span className={styles.circle} aria-hidden="true" />
        <span className={styles.label}>{children}</span>
      </RadioButton>
      {description ? (
        <Text slot="description" className={styles.description}>
          {description}
        </Text>
      ) : null}
    </RadioField>
  );
}

export interface ChoiceCardProps {
  value: string;
  /** Offering or plan name («Βασικό», «Άνετο», «Πλήρες»). */
  title: string;
  /** Formatted price («412,38 €»), shown in tabular figures. Never animated. */
  price?: string;
  /** Caption under the price («ετησίως», «σε 4 δόσεις»). */
  priceCaption?: string;
  /** 3–5 short bullets. */
  bullets?: string[];
  /** Optional overline tag («Συνιστάται»). */
  tag?: string;
  isDisabled?: boolean;
}

/**
 * Choice card (Part 2 §4.9) for offerings and payment plans: a radio whose name is the title and price and
 * whose description is the caption and bullets. Selected = 2 px accent border, `state.row-selected` fill and
 * a check badge; hover `elevation.2`.
 */
export function ChoiceCard({
  value,
  title,
  price,
  priceCaption,
  bullets = [],
  tag,
  isDisabled,
}: ChoiceCardProps) {
  const base = useId();
  const titleId = `${base}-title`;
  const priceId = `${base}-price`;
  const detailsId = `${base}-details`;
  return (
    <RadioField
      className={cx(styles.cardField)}
      value={value}
      aria-labelledby={joinIds(titleId, price ? priceId : undefined) ?? titleId}
      {...(priceCaption || bullets.length > 0 ? { 'aria-describedby': detailsId } : {})}
      {...(isDisabled ? { isDisabled } : {})}
    >
      <RadioButton className={cx(styles.card)}>
        <span className={styles.badge} aria-hidden="true">
          <Icon icon={Check} size={12} />
        </span>
        {tag ? <span className={styles.tag}>{tag}</span> : null}
        <span id={titleId} className={styles.cardTitle}>
          {title}
        </span>
        {price ? (
          <span id={priceId} className={styles.price}>
            {price}
          </span>
        ) : null}
        <span id={detailsId} className={styles.details}>
          {priceCaption ? <span className={styles.priceCaption}>{priceCaption}</span> : null}
          {bullets.length > 0 ? (
            <span className={styles.bullets}>
              {bullets.map((bullet) => (
                <span key={bullet} className={styles.bullet}>
                  <Icon icon={Check} size={14} className={styles.bulletIcon} />
                  <span>{bullet}</span>
                </span>
              ))}
            </span>
          ) : null}
        </span>
      </RadioButton>
    </RadioField>
  );
}
