import type { ProblemDetails } from '../../api/client';
import type { StepId } from './state';

/**
 * The rating and underwriting engines name the input they refuse by its path («vehicle.vehicleValue is required to
 * rate OWN-DAMAGE», «segments[…].riskTree.driver.dateOfBirth: …»). POL wraps that in POL-ERR-RATING; draft
 * validation uses POL-ERR-VALIDATION field errors. This maps those paths to the wizard step and field the user
 * has to change, so the banner can say what to do and take them there.
 */
export type InputField =
  | 'vehicleValue'
  | 'firstRegistrationYear'
  | 'engineCapacityCc'
  | 'vehicleDetails'
  | 'usage'
  | 'dateOfBirth'
  | 'licenceDate'
  | 'claims'
  | 'driver'
  | 'coverages'
  | 'effectiveDate'
  | 'unknown';

export interface InputFix {
  field: InputField;
  /** The step that holds the input; absent when the cause cannot be placed on a step. */
  step?: StepId;
  /** The cover the engine was rating when it refused (vehicle value). */
  cover?: string;
}

const codesWithInputPaths = new Set([
  'RAT-ERR-INPUT',
  'RAT-ERR-INPUT-UNDECLARED',
  'POL-ERR-RATING',
  'POL-ERR-VALIDATION',
  'UW-ERR-SNAPSHOT',
]);

/** Most specific first; matched against the problem detail and the field paths and codes of its errors. */
const rules: { field: InputField; step: StepId; pattern: RegExp }[] = [
  { field: 'vehicleValue', step: 'vehicle', pattern: /vehicleValue/ },
  { field: 'firstRegistrationYear', step: 'vehicle', pattern: /firstRegistrationYear/ },
  { field: 'engineCapacityCc', step: 'vehicle', pattern: /engineCapacityCc|engineCc/ },
  {
    field: 'vehicleDetails',
    step: 'vehicle',
    pattern: /RATING_FIELDS_REQUIRED|riskTree\.vehicles/,
  },
  { field: 'usage', step: 'questions', pattern: /vehicle\.usage|\.usage\b/ },
  {
    field: 'dateOfBirth',
    step: 'driver',
    pattern: /dateOfBirth|BIRTH_DATE_MISSING|PARTY_NOT_FOUND/,
  },
  { field: 'licenceDate', step: 'driver', pattern: /licenceIssueDate|yearFirstLicensed/ },
  { field: 'claims', step: 'questions', pattern: /claimsLast5Years/ },
  { field: 'driver', step: 'driver', pattern: /MAIN_DRIVER_REQUIRED|riskTree\.drivers|\.driver\b/ },
  { field: 'coverages', step: 'covers', pattern: /\.coverages|riskTree\.coverages/ },
  {
    field: 'effectiveDate',
    step: 'policyholder',
    pattern: /effectiveDate|effectiveAt|validPeriod|ValidPeriod/,
  },
];

/** What is wrong with the input, or null when the problem is not about a rating or underwriting input. */
export function explainInputProblem(problem: ProblemDetails): InputFix | null {
  if (!problem.code || !codesWithInputPaths.has(problem.code)) return null;
  const detail = problem.detail ?? '';
  const text = [
    detail,
    ...(problem.errors ?? []).map((e) => `${e.field} ${e.code} ${e.message ?? ''}`),
  ].join(' ');
  // POL-ERR-RATING also wraps other failures (tax, rounding): only the engines' input refusals are explained here.
  if (problem.code === 'POL-ERR-RATING' && !/RAT-ERR-INPUT|UW-ERR-SNAPSHOT/.test(text)) return null;
  const rule = rules.find((r) => r.pattern.test(text));
  if (!rule) return { field: 'unknown' };
  const cover =
    rule.field === 'vehicleValue'
      ? /\brate\s+([A-Z0-9-]+?)\.?(?:\s|$)/.exec(detail)?.[1]
      : undefined;
  return { field: rule.field, step: rule.step, ...(cover ? { cover } : {}) };
}

/** The i18n key (quote namespace) of the label of the field an input problem is about, when it has one. */
export const inputFieldLabelKeys: Partial<Record<InputField, string>> = {
  vehicleValue: 'vehicle.value',
  firstRegistrationYear: 'vehicle.year',
  engineCapacityCc: 'vehicle.engine',
  vehicleDetails: 'vehicle.year',
  licenceDate: 'driver.yearFirstLicensed',
  effectiveDate: 'policyholder.startDate',
};

/** The label of the field the problem is about, in the current language. */
export function fieldLabelOf(
  fix: InputFix,
  t: (key: string) => string,
  questionLabels: Partial<Record<InputField, string>>,
): string | undefined {
  const key = inputFieldLabelKeys[fix.field];
  return key ? t(key) : questionLabels[fix.field];
}

/**
 * Moves keyboard focus to the control labelled `text` on the page: a field (its label points at the input) or a
 * radio group. Returns whether something was focused.
 */
export function focusFieldByLabel(text: string): boolean {
  const norm = (s: string | null) => (s ?? '').replace(/\s+/g, ' ').replace('*', '').trim();
  const wanted = norm(text);
  for (const label of document.querySelectorAll('label')) {
    if (!norm(label.textContent).startsWith(wanted)) continue;
    const control = label.control;
    if (control instanceof HTMLElement) {
      control.focus();
      return true;
    }
  }
  for (const group of document.querySelectorAll('[role="radiogroup"]')) {
    const labelledBy = group.getAttribute('aria-labelledby');
    const labelText = labelledBy
      ? labelledBy
          .split(' ')
          .map((id) => document.getElementById(id)?.textContent)
          .join(' ')
      : (group.getAttribute('aria-label') ?? '');
    if (!norm(labelText).startsWith(wanted)) continue;
    const target = group.querySelector<HTMLElement>('input:checked, input, [role="radio"]');
    if (target) {
      target.focus();
      return true;
    }
  }
  return false;
}
