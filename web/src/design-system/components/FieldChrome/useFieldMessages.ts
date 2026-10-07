import { useId, type ReactNode } from 'react';

export interface FieldChromeMessagesInput {
  /** Helper text («ηη/μμ/εεεε»). Hidden while an error or warning shows (helper OR error). */
  description?: ReactNode;
  /** Error message (formula: what + where + how to fix). Sets the invalid state. */
  errorMessage?: ReactNode;
  /** Soft warning: shown and described, does not block. */
  warning?: ReactNode;
  /** Neutral information hint, e.g. «Ερμηνεύτηκε ως 1.234,56 €». */
  info?: ReactNode;
  /** Disabled with a reason: the control stays focusable with `aria-disabled` and this description. */
  disabledReason?: string | undefined;
  /** Extra description ids appended last (e.g. a visually hidden unit). */
  extraDescribedBy?: string | undefined;
}

export interface FieldChromeMessages {
  errorId: string;
  warningId: string;
  infoId: string;
  helperId: string;
  reasonId: string;
  showError: boolean;
  showWarning: boolean;
  showInfo: boolean;
  showHelper: boolean;
  showReason: boolean;
  /** `aria-describedby` with the error id first (DESIGN-B E.2). */
  describedBy: string | undefined;
}

function present(node: ReactNode): boolean {
  return node !== undefined && node !== null && node !== false && node !== '';
}

/**
 * Ids and visibility for a field's messages. Order in `aria-describedby`: error, warning, info, disabled
 * reason, helper, extra.
 */
export function useFieldMessages(input: FieldChromeMessagesInput): FieldChromeMessages {
  const base = useId();
  const showError = present(input.errorMessage);
  const showWarning = !showError && present(input.warning);
  const showInfo = present(input.info);
  const showReason = present(input.disabledReason);
  const showHelper = !showError && !showWarning && !showReason && present(input.description);
  const ids = {
    errorId: `${base}-error`,
    warningId: `${base}-warning`,
    infoId: `${base}-info`,
    helperId: `${base}-helper`,
    reasonId: `${base}-reason`,
  };
  const describedBy =
    [
      showError ? ids.errorId : null,
      showWarning ? ids.warningId : null,
      showInfo ? ids.infoId : null,
      showReason ? ids.reasonId : null,
      showHelper ? ids.helperId : null,
      input.extraDescribedBy ?? null,
    ]
      .filter(Boolean)
      .join(' ') || undefined;
  return { ...ids, showError, showWarning, showInfo, showHelper, showReason, describedBy };
}
