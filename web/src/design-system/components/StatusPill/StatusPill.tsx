import { useEffect, useState } from 'react';
import { Focusable } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { Icon } from '../../icons';
import { Spinner } from '../Spinner';
import { Tooltip } from '../Tooltip';
import { getStatusDefinition, statusId, type StatusRef } from './statusMap';
import styles from './StatusPill.module.css';

export type StatusPillVariant = 'status' | 'status-solid' | 'status-outline' | 'dot';
export type StatusPillSize = 'sm' | 'md';

export interface StatusPillOptions {
  /**
   * `status` (family bg/fg/border), `status-solid` (danger fill; only honoured for the solid states
   * breached, conflict and screening trueMatch, which are solid by default), `status-outline` (AI-generated,
   * IB-29 logs), `dot` (8 px dot; the label is in a tooltip and the accessible name unless `showLabel`).
   */
  variant?: StatusPillVariant;
  size?: StatusPillSize;
  /** «· sub-label», e.g. the approver group of «Αναμένει έγκριση · Ομάδα Β». */
  subLabel?: string;
  /** Countdown in days («6 ημ.»); updates without animation. */
  countdownDays?: number;
  /** The status is changing: the icon becomes a 12 px spinner. */
  isPending?: boolean;
  /** Dot variant only: show the label next to the dot (sidebars) instead of in a tooltip (narrow columns). */
  showLabel?: boolean;
  /** Announce changes politely («Κατάσταση: Σε ισχύ»). On by default; turn off for bulk updates. */
  announceChanges?: boolean;
}

export type StatusPillProps = StatusRef & StatusPillOptions;

function resolveVariant(
  requested: StatusPillVariant,
  solid: boolean,
  outline: boolean,
): StatusPillVariant {
  if (requested === 'dot' || requested === 'status-outline') return requested;
  // Solid is reserved for the critical states in the map; anything else falls back to the family pill.
  if (solid) return 'status-solid';
  if (outline) return 'status-outline';
  return 'status';
}

/**
 * Status pill (Part 2 §4.13): colour + icon + text from the single status map (rule 8). Never interactive;
 * a clickable status is a filter chip (`Tags`). Changes animate (MI-15) and are announced politely.
 *
 * `<StatusPill entity="policyTerm" state="inForce" />` · `<StatusPill semantic="breached" />`
 */
export function StatusPill(props: StatusPillProps) {
  const {
    variant = 'status',
    size = 'md',
    subLabel,
    countdownDays,
    isPending = false,
    showLabel = false,
    announceChanges = true,
  } = props;
  const { t } = useTranslation('ds');
  const ref: StatusRef =
    props.semantic !== undefined
      ? { semantic: props.semantic }
      : ({ entity: props.entity, state: props.state } as StatusRef);
  const definition = getStatusDefinition(ref);
  const id = statusId(ref);
  const label = t(definition.labelKey);

  // MI-15: detect a change of status during render and remember it, so the pill re-mounts its content
  // (the CSS morph plays) and the change is announced once.
  const [shownId, setShownId] = useState(id);
  const [change, setChange] = useState<{ count: number; label: string } | null>(null);
  if (shownId !== id) {
    setShownId(id);
    setChange((previous) => ({ count: (previous?.count ?? 0) + 1, label }));
  }
  useEffect(() => {
    if (change && announceChanges) {
      announce(t('statusPill.announce', { label: change.label }));
    }
  }, [change, announceChanges, t]);

  const resolved = resolveVariant(variant, definition.solid === true, definition.outline === true);
  const iconSize = size === 'sm' ? 12 : 14;
  const countdownShort =
    countdownDays === undefined ? null : t('statusPill.countdownShort', { count: countdownDays });
  const countdownLong =
    countdownDays === undefined ? null : t('statusPill.countdownLong', { count: countdownDays });

  const glyph = isPending ? (
    <span className={styles.icon} aria-hidden="true">
      <Spinner size={12} delayed={false} label={null} />
    </span>
  ) : (
    <span className={styles.icon} data-spins={definition.spins ? true : undefined}>
      <Icon icon={definition.icon} size={iconSize} />
    </span>
  );

  if (resolved === 'dot') {
    const name = [label, subLabel, countdownLong].filter(Boolean).join(' · ');
    const dot = (
      <span
        className={styles.dotPill}
        data-family={definition.family}
        data-size={size}
        data-changed={change ? true : undefined}
      >
        <span
          key={change?.count ?? 0}
          className={styles.dot}
          {...(showLabel ? { 'aria-hidden': true } : {})}
        />
        {showLabel ? (
          <span className={styles.dotLabel}>
            {label}
            {subLabel ? <span className={styles.subLabel}> · {subLabel}</span> : null}
            {countdownLong ? <span className="ds-visually-hidden"> · {countdownLong}</span> : null}
          </span>
        ) : null}
      </span>
    );
    if (showLabel) return dot;
    return (
      <Tooltip content={name}>
        <Focusable>
          <span
            role="img"
            aria-label={t('statusPill.announce', { label: name })}
            className={styles.dotTrigger}
          >
            {dot}
          </span>
        </Focusable>
      </Tooltip>
    );
  }

  return (
    <span
      className={styles.pill}
      data-variant={resolved}
      data-family={definition.family}
      data-size={size}
      data-flag={definition.flag ? true : undefined}
      data-pending={isPending || undefined}
      aria-busy={isPending || undefined}
    >
      <span
        key={change?.count ?? 0}
        className={styles.content}
        data-changed={change ? true : undefined}
      >
        {glyph}
        <span className={styles.label}>{label}</span>
        {subLabel ? <span className={styles.subLabel}>· {subLabel}</span> : null}
        {countdownShort ? (
          <span className={styles.countdown}>
            <span aria-hidden="true">· {countdownShort}</span>
            <span className="ds-visually-hidden">· {countdownLong}</span>
          </span>
        ) : null}
      </span>
    </span>
  );
}
