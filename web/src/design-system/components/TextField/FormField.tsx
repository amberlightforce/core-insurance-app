import { CircleAlert, CircleHelp, TriangleAlert } from 'lucide-react';
import { useMemo, type ReactNode, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Button as AriaButton,
  Dialog,
  DialogTrigger,
  Label,
  Popover,
  Tooltip as AriaTooltip,
  TooltipTriggerStateContext,
  type TooltipTriggerState,
} from 'react-aria-components';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { hasText, type FieldIds } from './fieldHooks';
import styles from './FormField.module.css';

export interface FieldLabelProps {
  children: ReactNode;
  isRequired?: boolean | undefined;
  /** Content of the optional `circle-help` popover next to the label. */
  help?: ReactNode;
  /** Plain-text label used in the help button's accessible name. */
  labelText?: string | undefined;
  /** Render as a plain `<span>` (for groups whose label is referenced with `aria-labelledby`). */
  elementType?: 'label' | 'span';
  id?: string;
  htmlFor?: string;
}

/**
 * Field label (Part 2 §4.2): `type.label`, `text.secondary`, wraps to 2 lines and never truncates. The
 * required marker is a visible `*` in `danger.fg` plus screen-reader text «υποχρεωτικό». Inside a React
 * Aria field the label is wired to the control automatically.
 */
export function FieldLabel({
  children,
  isRequired,
  help,
  labelText,
  elementType = 'label',
  id,
  htmlFor,
}: FieldLabelProps) {
  const { t } = useTranslation('ds');
  return (
    <div className={styles.labelRow}>
      <Label
        className={cx(styles.label)}
        elementType={elementType}
        {...(id ? { id } : {})}
        {...(htmlFor ? { htmlFor } : {})}
      >
        {children}
        {isRequired ? (
          <>
            {' '}
            <span className={styles.required} aria-hidden="true">
              *
            </span>
            <span className="ds-visually-hidden">{t('formField.required')}</span>
          </>
        ) : null}
      </Label>
      {help !== undefined && help !== null ? (
        <DialogTrigger>
          <AriaButton
            className={cx(styles.helpButton)}
            aria-label={t('formField.help', {
              label: labelText ?? (typeof children === 'string' ? children : ''),
            })}
          >
            <Icon icon={CircleHelp} size={14} />
          </AriaButton>
          <Popover
            className={cx(styles.popover)}
            data-material="popover"
            placement="bottom start"
            offset={6}
          >
            <Dialog
              className={cx(styles.helpDialog)}
              aria-label={t('formField.help', {
                label: labelText ?? (typeof children === 'string' ? children : ''),
              })}
            >
              {help}
            </Dialog>
          </Popover>
        </DialogTrigger>
      ) : null}
    </div>
  );
}

export interface FieldMessagesProps {
  ids: FieldIds;
  errorMessage?: string | undefined;
  warningMessage?: string | undefined;
  helperText?: string | undefined;
  /** Short-lived information (for example the plate conversion hint); polite. */
  transientMessage?: string | undefined;
  disabledReason?: string | undefined;
  /** Character count element (right-aligned on the message row). */
  count?: ReactNode;
}

/**
 * Helper **or** error (`type.caption`), plus warning, transient hint and character count. MI-14: the error
 * row expands and fades in; the icon scales 0.8 → 1.
 */
export function FieldMessages({
  ids,
  errorMessage,
  warningMessage,
  helperText,
  transientMessage,
  disabledReason,
  count,
}: FieldMessagesProps) {
  const error = hasText(errorMessage);
  const warning = !error && hasText(warningMessage);
  const helper = !error && hasText(helperText);
  const transient = hasText(transientMessage);
  const any = error || warning || helper || transient || count !== undefined;
  return (
    <>
      {any ? (
        <div className={styles.messages}>
          <div className={styles.messageStack}>
            {error ? (
              <p id={ids.error} className={styles.error}>
                <Icon icon={CircleAlert} size={14} className={styles.messageIcon} />
                <span>{errorMessage}</span>
              </p>
            ) : null}
            {warning ? (
              <p id={ids.warning} className={styles.warning}>
                <Icon icon={TriangleAlert} size={14} className={styles.messageIcon} />
                <span>{warningMessage}</span>
              </p>
            ) : null}
            {helper ? (
              <p id={ids.helper} className={styles.helper}>
                {helperText}
              </p>
            ) : null}
            {transient ? (
              <p id={ids.transient} className={styles.transient} role="status">
                {transientMessage}
              </p>
            ) : null}
          </div>
          {count}
        </div>
      ) : null}
      {hasText(disabledReason) ? (
        <span id={ids.reason} hidden>
          {disabledReason}
        </span>
      ) : null}
    </>
  );
}

export interface FormFieldProps extends Omit<FieldLabelProps, 'children'> {
  label: ReactNode;
  ids: FieldIds;
  children: ReactNode;
  errorMessage?: string | undefined;
  warningMessage?: string | undefined;
  helperText?: string | undefined;
  transientMessage?: string | undefined;
  disabledReason?: string | undefined;
  count?: ReactNode;
  className?: string | undefined;
}

/**
 * FormField anatomy (Part 2 §4.2): label + `*` → optional help popover → control → helper or error →
 * optional character count. Render it inside a React Aria field (TextField, Select, …) so that the label is
 * wired automatically, or pass `htmlFor` for a custom control; wire `aria-describedby` with `ids`.
 */
export function FormField({
  label,
  ids,
  children,
  errorMessage,
  warningMessage,
  helperText,
  transientMessage,
  disabledReason,
  count,
  className,
  ...labelProps
}: FormFieldProps) {
  return (
    <div className={cx(styles.field, className)}>
      <FieldLabel {...labelProps}>{label}</FieldLabel>
      {children}
      <FieldMessages
        ids={ids}
        errorMessage={errorMessage}
        warningMessage={warningMessage}
        helperText={helperText}
        transientMessage={transientMessage}
        disabledReason={disabledReason}
        count={count}
      />
    </div>
  );
}

export interface AnchoredTooltipProps {
  /** Element the tooltip points at. */
  triggerRef: RefObject<Element | null>;
  isOpen: boolean;
  children: ReactNode;
  placement?: 'top' | 'bottom';
}

/**
 * A tooltip anchored to an element that is not a React Aria trigger: disabled-with-reason controls (shown
 * on hover and focus) and the MI-10 «Αντιγράφηκε» confirmation. Solid inverse surface, never glass.
 */
export function AnchoredTooltip({
  triggerRef,
  isOpen,
  children,
  placement = 'top',
}: AnchoredTooltipProps) {
  // React Aria's Tooltip reads its open state from a trigger context; this tooltip has no trigger.
  const state = useMemo<TooltipTriggerState>(
    () => ({
      isOpen,
      shouldSkipAnimation: false,
      open: () => undefined,
      close: () => undefined,
    }),
    [isOpen],
  );
  return (
    <TooltipTriggerStateContext.Provider value={state}>
      <AriaTooltip
        className={cx(styles.tooltip)}
        triggerRef={triggerRef}
        isOpen={isOpen}
        placement={placement}
        offset={8}
      >
        {children}
      </AriaTooltip>
    </TooltipTriggerStateContext.Provider>
  );
}
