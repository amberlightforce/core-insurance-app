import { Check, CircleAlert, Lock, TriangleAlert } from 'lucide-react';
import { useId } from 'react';
import { Button as AriaButton, Link } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { formatInteger } from '../../../format/numbers';
import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx } from '../../utils/cx';
import { Spinner } from '../Spinner';
import { Tooltip } from '../Tooltip';
import {
  defaultCanNavigateTo,
  horizontalWindow,
  type CanNavigateTo,
  type StepItem,
} from './stepperLogic';
import styles from './Stepper.module.css';
import { useStepperShortcuts } from './useStepperShortcuts';

export type StepperOrientation = 'vertical' | 'horizontal';

export interface StepperProps {
  steps: readonly StepItem[];
  currentId: string;
  onNavigate?: (id: string) => void;
  /** Which steps can be opened (default: completed, warning and error steps; upcoming once all before are valid). */
  canNavigateTo?: CanNavigateTo;
  /** Vertical at ≥ 1240 px (220 px wide, 40 px steps); horizontal below (current ±1 + «3 / 7»). */
  orientation?: StepperOrientation;
  /** Alt+→/← move between steps from anywhere (on by default when `onNavigate` is given). */
  shortcuts?: boolean;
  /** Landmark name (default «Βήματα»). */
  'aria-label'?: string;
}

type NodeState = StepItem['state'] | 'current';

function Node({
  state,
  index,
  errorCount,
}: {
  state: NodeState;
  index: number;
  errorCount?: number;
}) {
  const region = useRegionFormat();
  switch (state) {
    case 'complete':
      return <Icon icon={Check} size={14} />;
    case 'error':
      return (
        <span className={styles.nodeError}>
          <Icon icon={CircleAlert} size={12} />
          {errorCount ? <span>{formatInteger(errorCount, region)}</span> : null}
        </span>
      );
    case 'warning':
      return <Icon icon={TriangleAlert} size={14} />;
    case 'locked':
      return <Icon icon={Lock} size={14} />;
    case 'loading':
      return <Spinner size={14} delayed={false} label={null} />;
    case 'current':
    case 'upcoming':
      return <span>{formatInteger(index + 1, region)}</span>;
  }
}

interface StepProps {
  step: StepItem;
  index: number;
  isCurrent: boolean;
  navigable: boolean;
  onNavigate: ((id: string) => void) | undefined;
  isLast: boolean;
}

function Step({ step, index, isCurrent, navigable, onNavigate, isLast }: StepProps) {
  const { t } = useTranslation('ds');
  const subId = useId();
  const reasonId = useId();
  const nodeState: NodeState = isCurrent && step.state !== 'loading' ? 'current' : step.state;
  const stateText = t(`stepper.state.${nodeState}`);
  const name = t('stepper.stepName', { index: index + 1, label: step.label, state: stateText });
  const reason = navigable
    ? undefined
    : step.state === 'locked'
      ? step.lockedReason
      : isCurrent
        ? undefined
        : t('stepper.notReachable');
  const describedBy = [step.subLabel ? subId : null, reason ? reasonId : null]
    .filter(Boolean)
    .join(' ');

  const content = (
    <>
      <span className={styles.node} data-state={nodeState} aria-hidden="true">
        <Node
          state={nodeState}
          index={index}
          {...(step.errorCount ? { errorCount: step.errorCount } : {})}
        />
      </span>
      <span className={styles.text} aria-hidden="true">
        <span className={styles.label}>{step.label}</span>
        {step.subLabel ? <span className={styles.subLabel}>{step.subLabel}</span> : null}
      </span>
    </>
  );

  const common = {
    className: cx(styles.step),
    'aria-label': name,
    'data-state': nodeState,
    ...(isCurrent ? { 'aria-current': 'step' as const } : {}),
    ...(describedBy ? { 'aria-describedby': describedBy } : {}),
  };

  const control =
    navigable && step.href && !isCurrent ? (
      <Link {...common} href={step.href}>
        {content}
      </Link>
    ) : (
      <AriaButton
        {...common}
        {...(navigable && !isCurrent ? {} : { 'aria-disabled': true })}
        data-blocked={!navigable && !isCurrent ? true : undefined}
        onPress={() => {
          if (navigable && !isCurrent) onNavigate?.(step.id);
        }}
      >
        {content}
      </AriaButton>
    );

  return (
    <li
      className={styles.item}
      data-state={nodeState}
      data-complete={step.state === 'complete' || undefined}
      data-last={isLast || undefined}
    >
      {reason ? <Tooltip content={reason}>{control}</Tooltip> : control}
      {step.subLabel ? (
        <span id={subId} hidden>
          {step.subLabel}
        </span>
      ) : null}
      {reason ? (
        <span id={reasonId} hidden>
          {reason}
        </span>
      ) : null}
      {/* MI-38: the connector after a completed step fills with gradient.tide (scale only). */}
      {isLast ? null : (
        <span className={styles.connector} aria-hidden="true">
          <span className={styles.connectorFill} />
        </span>
      )}
    </li>
  );
}

/**
 * Stepper (Part 2 §4.23): `<nav>` + ordered list; each step is a RAC Link or Button with
 * `aria-current="step"` on the current step and its state in the accessible name
 * («Βήμα 3, Οχήματα, ολοκληρωμένο»). Steps that cannot be opened stay focusable with the reason.
 */
export function Stepper({
  steps,
  currentId,
  onNavigate,
  canNavigateTo = defaultCanNavigateTo,
  orientation = 'vertical',
  shortcuts = true,
  ...rest
}: StepperProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  useStepperShortcuts({
    steps,
    currentId,
    onNavigate: (id) => onNavigate?.(id),
    canNavigateTo,
    isEnabled: shortcuts && onNavigate !== undefined,
  });
  const currentIndex = Math.max(
    0,
    steps.findIndex((step) => step.id === currentId),
  );
  const indices =
    orientation === 'horizontal' ? horizontalWindow(steps, currentIndex) : steps.map((_, i) => i);

  return (
    <nav
      aria-label={rest['aria-label'] ?? t('stepper.label')}
      className={styles.stepper}
      data-orientation={orientation}
    >
      <ol className={styles.list}>
        {indices.map((index, position) => {
          const step = steps[index];
          if (!step) return null;
          return (
            <Step
              key={step.id}
              step={step}
              index={index}
              isCurrent={step.id === currentId}
              navigable={canNavigateTo(step, index, steps)}
              onNavigate={onNavigate}
              isLast={position === indices.length - 1}
            />
          );
        })}
      </ol>
      {orientation === 'horizontal' ? (
        <span className={styles.counter}>
          <span aria-hidden="true">
            {t('stepper.counter', {
              current: formatInteger(currentIndex + 1, region),
              total: formatInteger(steps.length, region),
            })}
          </span>
          <span className="ds-visually-hidden">
            {t('stepper.counterLong', {
              current: formatInteger(currentIndex + 1, region),
              total: formatInteger(steps.length, region),
            })}
          </span>
        </span>
      ) : null}
    </nav>
  );
}
