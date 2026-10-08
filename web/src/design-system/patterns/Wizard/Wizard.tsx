import { ArrowLeft, ArrowRight, UserCheck } from 'lucide-react';
import { useEffect, useId, useRef, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

import { Button } from '../../components/Button';
import { matchesShortcut } from '../../components/Kbd';
import { Stepper, type StepItem } from '../../components/Stepper';
import { useRegionFormat } from '../../preferences';
import { minWidthQuery } from '../../tokens';
import { cx } from '../../utils/cx';
import { formatTime } from '../../../format/dates';
import { useMediaQuery } from './useMediaQuery';
import styles from './Wizard.module.css';

export interface WizardCommit {
  /** Verb + object: «Δέσμευση», «Έκδοση ασφαλιστηρίου», «Υποβολή αναγγελίας ζημίας». */
  label: string;
  onCommit: () => unknown;
  /** The exact blocker («Δεν μπορεί να γίνει δέσμευση: 2 ανοιχτά ζητήματα ανάληψης»); the CTA stays visible. */
  disabledReason?: string;
  isLoading?: boolean;
  /** «Αποστολή για έγκριση» wording when the action needs approval. */
  needsApproval?: boolean;
}

export interface WizardProps {
  /** Wizard title, e.g. «Νέα προσφορά · Αυτοκίνητο». */
  title: string;
  steps: readonly StepItem[];
  currentId: string;
  onNavigate: (id: string) => void;
  /** Step body. */
  children: ReactNode;
  /** The commit CTA, shown on the last step (the one commit per view). */
  commit: WizardCommit;
  /** Next is blocked with a reason (for example invalid fields on this step). */
  nextDisabledReason?: string;
  onSaveDraft?: () => unknown;
  isSavingDraft?: boolean;
  lastSavedAt?: Date | null;
  isDirty?: boolean;
  /** Running summary (premium with no ticker, UW issues, documents): sticky 320 px at ≥1440, bottom bar below. */
  summary?: ReactNode;
  /** `wide` for risk tables (full width); forms default to max 960 px. */
  bodyWidth?: 'form' | 'wide';
}

/**
 * Wizard pattern (Part 1 §3.4, Part 3 §5.1): stepper (vertical at ≥1240, horizontal below), step body,
 * optional sticky summary, and an opaque sticky footer with «Αποθήκευση πρόχειρου» + last saved time on the
 * left and Πίσω / Επόμενο / the commit CTA on the right. Ctrl/⌘+Enter commits from anywhere in the last step
 * (or moves to the next step); Alt+←/→ move between steps (Stepper shortcuts).
 */
export function Wizard({
  title,
  steps,
  currentId,
  onNavigate,
  children,
  commit,
  nextDisabledReason,
  onSaveDraft,
  isSavingDraft = false,
  lastSavedAt,
  isDirty = false,
  summary,
  bodyWidth = 'form',
}: WizardProps) {
  const { t } = useTranslation('ds');
  const region = useRegionFormat();
  const wide = useMediaQuery(minWidthQuery('lg'));
  const rootRef = useRef<HTMLDivElement>(null);
  const bodyRef = useRef<HTMLDivElement>(null);
  const index = steps.findIndex((s) => s.id === currentId);
  const isFirst = index <= 0;
  const isLast = index === steps.length - 1;
  const previous = steps[index - 1];
  const next = steps[index + 1];
  const shownId = useRef(currentId);
  const headingId = useId();

  // Move focus to the new step's heading so keyboard and screen-reader users land in the step (MI-38). Only on a
  // real step change: comparing ids (not a first-render flag) keeps StrictMode's repeated effects from stealing
  // focus and scrolling the page on load.
  useEffect(() => {
    if (shownId.current === currentId) return;
    shownId.current = currentId;
    bodyRef.current?.focus();
  }, [currentId]);

  const goNext = () => {
    if (next && !nextDisabledReason) onNavigate(next.id);
  };
  const runCommit = () => {
    if (!commit.disabledReason && !commit.isLoading) void commit.onCommit();
  };

  useEffect(() => {
    const root = rootRef.current;
    if (!root) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (!matchesShortcut(event, 'Mod+Enter')) return;
      event.preventDefault();
      if (isLast) runCommit();
      else goNext();
    };
    root.addEventListener('keydown', onKeyDown);
    return () => {
      root.removeEventListener('keydown', onKeyDown);
    };
  });

  const current = steps[index];
  const savedText = isDirty
    ? t('wizard.unsaved')
    : lastSavedAt
      ? t('wizard.savedAt', { time: formatTime(lastSavedAt, region) })
      : null;

  return (
    <div
      ref={rootRef}
      className={cx(styles.wizard)}
      data-wide={wide || undefined}
      data-summary={summary ? true : undefined}
    >
      <div className={cx(styles.stepper)}>
        <Stepper
          steps={steps}
          currentId={currentId}
          onNavigate={onNavigate}
          orientation={wide ? 'vertical' : 'horizontal'}
        />
      </div>
      <section className={cx(styles.main)} aria-labelledby={headingId}>
        <div ref={bodyRef} className={cx(styles.body)} data-width={bodyWidth} tabIndex={-1}>
          <p className="ds-overline">{title}</p>
          <h2 id={headingId} className="ds-heading-2">
            {current?.label}
          </h2>
          {children}
        </div>
      </section>
      {summary ? (
        <aside className={cx(styles.summary)} aria-label={t('wizard.summary')}>
          {summary}
        </aside>
      ) : null}
      <footer className={cx(styles.footer)}>
        <div className={cx(styles.footerStart)}>
          {onSaveDraft ? (
            <Button
              variant="secondary"
              isLoading={isSavingDraft}
              onPress={() => void onSaveDraft()}
            >
              {t('wizard.saveDraft')}
            </Button>
          ) : null}
          {savedText ? (
            <span className={cx(styles.saved)} data-dirty={isDirty || undefined} role="status">
              {isDirty ? <span className={cx(styles.dirtyDot)} aria-hidden="true" /> : null}
              {savedText}
            </span>
          ) : null}
        </div>
        <div className={cx(styles.footerEnd)}>
          {!isFirst && previous ? (
            <Button
              variant="secondary"
              icon={ArrowLeft}
              onPress={() => {
                onNavigate(previous.id);
              }}
            >
              {t('wizard.back')}
            </Button>
          ) : null}
          {isLast ? (
            <Button
              variant="commit"
              size="md"
              shortcut="Mod+Enter"
              isLoading={commit.isLoading ?? false}
              {...(commit.needsApproval ? { icon: UserCheck } : {})}
              {...(commit.disabledReason ? { disabledReason: commit.disabledReason } : {})}
              onPress={runCommit}
            >
              {commit.needsApproval ? t('wizard.sendForApproval') : commit.label}
            </Button>
          ) : (
            <Button
              variant="primary"
              trailingIcon={ArrowRight}
              shortcut="Mod+Enter"
              {...(nextDisabledReason ? { disabledReason: nextDisabledReason } : {})}
              onPress={goNext}
            >
              {t('wizard.next')}
            </Button>
          )}
        </div>
      </footer>
    </div>
  );
}
