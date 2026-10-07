import { CircleAlert, RefreshCw, Sparkles, Square } from 'lucide-react';
import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import {
  Button as AriaButton,
  Focusable,
  Menu,
  MenuItem,
  MenuTrigger,
  Popover,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import { formatNumber } from '../KpiTile/format';
import { Heading, type HeadingLevel } from '../States/Heading';
import { Tooltip } from '../Tooltip';
import styles from './AiSuggestion.module.css';

export type AiSuggestionState =
  | 'generating'
  | 'proposed'
  | 'accepted'
  | 'edited'
  | 'rejected'
  | 'expired'
  | 'low-confidence'
  | 'error'
  | 'disabled';

export interface AiReceipt {
  /** Who decided: «Κώστα Νικολάου» (as it should read after «από»). */
  name: string;
  /** Already formatted time: «14:32». */
  time: string;
}

export interface AiSuggestionCardProps {
  state: AiSuggestionState;
  /** Card title; «Σύνοψη ΤΝ» by default. */
  title?: string;
  headingLevel?: HeadingLevel;
  /** Model confidence 0–1. Below 0.6 the chip uses the warning family. */
  confidence?: number;
  /** Model name and version, shown in the confidence tooltip. */
  model?: string;
  /** The suggestion (use `AiCitation` for superscript [n] sources). */
  children?: ReactNode;
  sourcesCount?: number;
  onAccept?: () => void;
  onEdit?: () => void;
  onReject?: () => void;
  onWhy?: () => void;
  onSources?: () => void;
  /** Generating: «Διακοπή». */
  onStop?: () => void;
  /** Accepted, edited or rejected: «Αναίρεση». */
  onUndo?: () => void;
  /** Expired: «Νέα πρόταση». */
  onRefresh?: () => void;
  receipt?: AiReceipt;
  rejectReason?: string;
  /** Reason menu after a rejection (when no reason is recorded yet). */
  rejectReasons?: { id: string; label: string }[];
  onRejectReason?: (id: string) => void;
}

const THINKING_LIMIT_MS = 10_000;

function confidenceLevel(confidence: number): 'high' | 'medium' | 'low' {
  if (confidence >= 0.8) return 'high';
  if (confidence >= 0.6) return 'medium';
  return 'low';
}

/**
 * AI suggestion card (Part 2 §4.37, IB-09/22): Iris edge on an ai-tinted fill, «Σύνοψη ΤΝ» + confidence,
 * citations, Αποδοχή / Επεξεργασία / Απόρριψη / Γιατί; / Πηγές. Nothing enters the record without a human
 * action. `disabled` (kill switch) renders nothing, so the surrounding layout must not leave a hole.
 */
export function AiSuggestionCard({
  state,
  title,
  headingLevel = 3,
  confidence,
  model,
  children,
  sourcesCount,
  onAccept,
  onEdit,
  onReject,
  onWhy,
  onSources,
  onStop,
  onUndo,
  onRefresh,
  receipt,
  rejectReason,
  rejectReasons,
  onRejectReason,
}: AiSuggestionCardProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const titleId = useId();
  const [revealed, setRevealed] = useState(false);
  const [thinkingStopped, setThinkingStopped] = useState(false);
  const heading = title ?? t('aiSuggestion.summary');

  // MI-50: the thinking edge rotates for at most 10 s, then stays static.
  useEffect(() => {
    if (state !== 'generating') return;
    const id = setTimeout(() => {
      setThinkingStopped(true);
    }, THINKING_LIMIT_MS);
    return () => {
      clearTimeout(id);
      setThinkingStopped(false);
    };
  }, [state]);

  // Streaming text is announced only on completion (E.2).
  const previous = useRef(state);
  useEffect(() => {
    if (previous.current === 'generating' && state === 'proposed') {
      announce(t('aiSuggestion.ready', { title: heading }));
    }
    previous.current = state;
  }, [state, heading, t]);

  if (state === 'disabled') return null;

  if (state === 'low-confidence' && !revealed) {
    return (
      <Button
        variant="link"
        icon={Sparkles}
        onPress={() => {
          setRevealed(true);
        }}
      >
        {t('aiSuggestion.showLowConfidence')}
      </Button>
    );
  }

  if (state === 'error') {
    return (
      <div className={styles.unavailable} role="status">
        <Icon icon={Sparkles} size={14} />
        {t('aiSuggestion.unavailable')}
      </div>
    );
  }

  const level = confidence === undefined ? null : confidenceLevel(confidence);
  const confidenceValue = confidence === undefined ? '' : formatNumber(confidence, region, 2);
  const confidenceText =
    level === 'high'
      ? t('aiSuggestion.confidenceHigh', { value: confidenceValue })
      : level === 'medium'
        ? t('aiSuggestion.confidenceMedium', { value: confidenceValue })
        : level === 'low'
          ? t('aiSuggestion.confidenceLow', { value: confidenceValue })
          : null;
  const chip = confidenceText ? (
    <span className={styles.confidence} data-level={level}>
      {confidenceText}
    </span>
  ) : null;

  const decided = state === 'accepted' || state === 'edited' || state === 'rejected';

  return (
    <div
      className={styles.card}
      data-state={state}
      data-thinking-stopped={thinkingStopped || undefined}
      data-ai="true"
      role="group"
      aria-label={t('aiSuggestion.name', { title: heading })}
      aria-busy={state === 'generating' || undefined}
    >
      <div className={styles.header}>
        <span className={styles.sparkle}>
          <Icon icon={Sparkles} size={16} />
        </span>
        <Heading level={headingLevel} className={styles.title} id={titleId}>
          {heading}
        </Heading>
        {chip && model ? (
          <Tooltip content={model}>
            <Focusable>
              <span className={styles.chipTrigger} tabIndex={0}>
                {chip}
              </span>
            </Focusable>
          </Tooltip>
        ) : (
          chip
        )}
        {state === 'accepted' || state === 'edited' ? (
          <span className={styles.generatedPill}>{t('aiSuggestion.generated')}</span>
        ) : null}
        {state === 'edited' ? (
          <span className={styles.editedPill}>{t('aiSuggestion.edited')}</span>
        ) : null}
      </div>

      {state === 'expired' ? (
        <div className={styles.stale} role="status">
          <CircleAlertIcon />
          <span>{t('aiSuggestion.expired')}</span>
          {onRefresh ? (
            <Button variant="ghost" size="sm" icon={RefreshCw} onPress={onRefresh}>
              {t('aiSuggestion.refresh')}
            </Button>
          ) : null}
        </div>
      ) : null}

      {state === 'generating' ? (
        <div className={styles.generating}>
          <span className={styles.generatingText}>{t('aiSuggestion.generating')}</span>
          {children ? <div className={styles.body}>{children}</div> : null}
          {onStop ? (
            <div className={styles.footer}>
              <Button variant="secondary" size="sm" icon={Square} onPress={onStop}>
                {t('aiSuggestion.stop')}
              </Button>
            </div>
          ) : null}
        </div>
      ) : (
        <div
          className={styles.body}
          data-dimmed={state === 'expired' || state === 'rejected' || undefined}
        >
          {children}
        </div>
      )}

      {state === 'proposed' || state === 'low-confidence' ? (
        <div className={styles.footer}>
          {onAccept ? (
            <Button variant="ai" size="sm" onPress={onAccept}>
              {t('aiSuggestion.accept')}
            </Button>
          ) : null}
          {onEdit ? (
            <Button variant="secondary" size="sm" onPress={onEdit}>
              {t('aiSuggestion.edit')}
            </Button>
          ) : null}
          {onReject ? (
            <Button variant="ghost" size="sm" onPress={onReject}>
              {t('aiSuggestion.reject')}
            </Button>
          ) : null}
          {onWhy ? (
            <Button variant="link" size="sm" onPress={onWhy}>
              {t('aiSuggestion.why')}
            </Button>
          ) : null}
          {onSources && sourcesCount !== undefined ? (
            <Button variant="link" size="sm" onPress={onSources}>
              {t('aiSuggestion.sources', { count: sourcesCount })}
            </Button>
          ) : null}
        </div>
      ) : null}

      {decided && receipt ? (
        <div className={styles.receipt}>
          <span>
            {state === 'rejected'
              ? t('aiSuggestion.rejectedBy', { name: receipt.name, time: receipt.time })
              : t('aiSuggestion.acceptedBy', { name: receipt.name, time: receipt.time })}
          </span>
          {onUndo ? (
            <Button variant="link" size="sm" onPress={onUndo}>
              {t('aiSuggestion.undo')}
            </Button>
          ) : null}
        </div>
      ) : null}

      {state === 'rejected' ? (
        rejectReason ? (
          <p className={styles.reason}>{t('aiSuggestion.reason', { reason: rejectReason })}</p>
        ) : rejectReasons && rejectReasons.length > 0 && onRejectReason ? (
          <MenuTrigger>
            <AriaButton className={cx(styles.reasonTrigger)}>
              {t('aiSuggestion.addReason')}
            </AriaButton>
            <Popover
              className={cx(styles.menuPopover)}
              placement="bottom start"
              data-material="popover"
            >
              <Menu
                className={cx(styles.menu)}
                aria-label={t('aiSuggestion.addReason')}
                onAction={(key) => {
                  onRejectReason(String(key));
                }}
              >
                {rejectReasons.map((r) => (
                  <MenuItem key={r.id} id={r.id} className={cx(styles.menuItem)}>
                    {r.label}
                  </MenuItem>
                ))}
              </Menu>
            </Popover>
          </MenuTrigger>
        ) : null
      ) : null}
    </div>
  );
}

function CircleAlertIcon() {
  return (
    <span className={styles.staleIcon}>
      <Icon icon={CircleAlert} size={14} />
    </span>
  );
}
