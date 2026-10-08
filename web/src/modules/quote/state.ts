import type {
  CatalogueCoverage,
  DraftInstruction,
  JobQuoteResponse,
  Question,
} from '../../api/types';
import { validatePlate } from '../../format';

/**
 * The slice sells one product to one legal entity through the staff channel. These are configuration values the
 * API asks for in its request bodies; the staff portal has no tenant/entity profile yet (PLT user profile).
 */
export const slice = {
  jurisdiction: 'GR',
  legalEntity: 'GR-TEST',
  product: 'MOTOR-GR',
  channel: 'STAFF',
  questionSet: 'MOTOR-RISK',
  /** D-SLC-10c: the happy path pays the premium in one ANNUAL instalment. */
  paymentPlan: 'ANNUAL',
} as const;

export const stepIds = [
  'policyholder',
  'vehicle',
  'driver',
  'covers',
  'questions',
  'premium',
  'bind',
] as const;
export type StepId = (typeof stepIds)[number];

export interface PartyRef {
  partyId: string;
  /** Display name for the summary; held in memory only. */
  label: string;
  partyNumber?: string;
}

export interface VehicleForm {
  plate: string;
  vin: string;
  make: string;
  model: string;
  firstRegistrationYear: string;
  engineCapacityCc: string;
  powerKw: string;
  fuelType: string;
  /** Decimal string in minor-unit precision, or empty. */
  value: string;
  garagingPostcode: string;
}

export interface DriverForm {
  sameAsPolicyholder: boolean;
  party: PartyRef | null;
  yearFirstLicensed: string;
}

export interface CoverForm {
  selected: boolean;
  /** Term code → option code (OPTION_LIST) or decimal value (DIRECT). */
  terms: Record<string, string>;
}

export interface Draft {
  policyholder: PartyRef | null;
  startDate: string;
  vehicle: VehicleForm;
  driver: DriverForm;
  covers: Record<string, CoverForm>;
  answers: Record<string, string>;
}

export const emptyVehicle: VehicleForm = {
  plate: '',
  vin: '',
  make: '',
  model: '',
  firstRegistrationYear: '',
  engineCapacityCc: '',
  powerKw: '',
  fuelType: '',
  value: '',
  garagingPostcode: '',
};

export function emptyDraft(startDate: string): Draft {
  return {
    policyholder: null,
    startDate,
    vehicle: emptyVehicle,
    driver: { sameAsPolicyholder: true, party: null, yearFirstLicensed: '' },
    covers: {},
    answers: {},
  };
}

/** The server-side job this wizard works on. */
export interface JobRef {
  jobId: string;
  versionNo: number;
  draftVersion: number;
  vehicleLocator?: string;
  driverLocator?: string;
  /** The policyholder and start date the submission was created for; changing either starts a new submission. */
  createdFor: string;
}

export const fuelTypes = ['PETROL', 'DIESEL', 'LPG', 'ELECTRIC', 'HYBRID'] as const;

/* Validation ------------------------------------------------------------------------------------------- */

export type FieldIssue =
  'required' | 'plate' | 'year' | 'range' | 'postcode' | 'valueForCover' | 'integer';

/**
 * Covers whose rating base is the vehicle value (rating table BASE_RATE basis VEHICLE_VALUE): without the value
 * the rating engine refuses with RAT-ERR-INPUT «vehicle.vehicleValue is required to rate <cover>» (D-SLC-21).
 */
export const vehicleValueCovers: readonly string[] = ['OWN-DAMAGE'];

/** The selected covers that need the vehicle value to be rated. */
export function coversNeedingVehicleValue(
  coverages: readonly CatalogueCoverage[],
  covers: Record<string, CoverForm>,
): string[] {
  return coverages
    .filter(
      (c) =>
        vehicleValueCovers.includes(c.code) &&
        (c.existence === 'REQUIRED' || covers[c.code]?.selected === true),
    )
    .map((c) => c.code);
}

/** A vehicle value is a positive amount of at most 9 whole digits (RatingFacts.Money). */
export function vehicleValueIssue(value: string, needed: boolean): FieldIssue | undefined {
  const text = value.trim();
  if (text === '') return needed ? 'valueForCover' : undefined;
  const amount = Number(text);
  return Number.isFinite(amount) && amount > 0 && amount < 1_000_000_000 ? undefined : 'range';
}

export function isInteger(text: string): boolean {
  return /^\d+$/.test(text.trim());
}

export function validateVehicle(
  v: VehicleForm,
  thisYear: number,
): Partial<Record<keyof VehicleForm, FieldIssue>> {
  const issues: Partial<Record<keyof VehicleForm, FieldIssue>> = {};
  if (v.plate.trim() === '') issues.plate = 'required';
  else if (validatePlate(v.plate) !== null) issues.plate = 'plate';
  if (v.make.trim() === '') issues.make = 'required';
  if (v.model.trim() === '') issues.model = 'required';
  if (v.firstRegistrationYear.trim() === '') issues.firstRegistrationYear = 'required';
  else if (
    !isInteger(v.firstRegistrationYear) ||
    Number(v.firstRegistrationYear) < 1950 ||
    Number(v.firstRegistrationYear) > thisYear + 1
  ) {
    issues.firstRegistrationYear = 'year';
  }
  if (v.engineCapacityCc.trim() === '') issues.engineCapacityCc = 'required';
  else if (
    !isInteger(v.engineCapacityCc) ||
    Number(v.engineCapacityCc) < 1 ||
    Number(v.engineCapacityCc) > 20000
  ) {
    issues.engineCapacityCc = 'range';
  }
  if (v.powerKw.trim() !== '' && (!isInteger(v.powerKw) || Number(v.powerKw) < 1))
    issues.powerKw = 'range';
  if (v.garagingPostcode.trim() === '') issues.garagingPostcode = 'required';
  else if (!/^\d{5}$/.test(v.garagingPostcode.trim())) issues.garagingPostcode = 'postcode';
  return issues;
}

export function validateDriver(
  d: DriverForm,
  thisYear: number,
): { party?: FieldIssue; yearFirstLicensed?: FieldIssue } {
  const issues: { party?: FieldIssue; yearFirstLicensed?: FieldIssue } = {};
  if (!d.sameAsPolicyholder && !d.party) issues.party = 'required';
  if (d.yearFirstLicensed.trim() === '') issues.yearFirstLicensed = 'required';
  else if (
    !isInteger(d.yearFirstLicensed) ||
    Number(d.yearFirstLicensed) < 1900 ||
    Number(d.yearFirstLicensed) > thisYear
  ) {
    issues.yearFirstLicensed = 'year';
  }
  return issues;
}

/** Selected covers must have every required term filled (a limit or deductible chosen). */
export function missingCoverTerms(
  coverages: readonly CatalogueCoverage[],
  covers: Record<string, CoverForm>,
): string[] {
  const missing: string[] = [];
  for (const cover of coverages) {
    const form = covers[cover.code];
    if (cover.existence !== 'REQUIRED' && !form?.selected) continue;
    for (const term of cover.terms) {
      if (!term.required) continue;
      // A required term with a single option is preselected for the user.
      if (term.kind === 'OPTION_LIST' && (term.options?.length ?? 0) === 1) continue;
      const value = form?.terms[term.code];
      if (value === undefined || value === '') missing.push(`${cover.code}.${term.code}`);
    }
  }
  return missing;
}

/** Answers the rating reads as numbers must be whole numbers (claims 0..50, RatingFacts.Integer). */
export function invalidAnswers(
  questions: readonly Question[],
  answers: Record<string, string>,
): Record<string, FieldIssue> {
  const out: Record<string, FieldIssue> = {};
  for (const q of questions) {
    const value = (answers[q.code] ?? '').trim();
    if (value === '' || !questionState(q, answers).visible) continue;
    if (q.answerType === 'INTEGER' && !isInteger(value)) out[q.code] = 'integer';
    else if (q.answerType === 'DECIMAL' && !Number.isFinite(Number(value))) out[q.code] = 'range';
    else if (q.mapsToField === 'driver.claimsLast5Years' && Number(value) > 50)
      out[q.code] = 'range';
  }
  return out;
}

/** Whether a question is visible and whether it is required, given the current answers (PFC `visibleWhen`/`requiredWhen`). */
export function questionState(
  q: Question,
  answers: Record<string, string>,
): { visible: boolean; required: boolean } {
  const holds = (cond: Question['visibleWhen']) =>
    cond ? cond.answeredWith.includes(answers[cond.question] ?? '') : undefined;
  const visible = holds(q.visibleWhen) ?? true;
  const required = visible && (holds(q.requiredWhen) ?? q.required);
  return { visible, required };
}

export function unansweredRequired(
  questions: readonly Question[],
  answers: Record<string, string>,
): string[] {
  return questions
    .filter((q) => {
      const s = questionState(q, answers);
      return s.visible && s.required && (answers[q.code] ?? '').trim() === '';
    })
    .map((q) => q.code);
}

/** Knock-out answers chosen by the user (the question's answer outcome KNOCK_OUT). */
export function knockOuts(
  questions: readonly Question[],
  answers: Record<string, string>,
): string[] {
  return questions
    .filter((q) => {
      const chosen = q.answers?.find((a) => a.code === answers[q.code]);
      return questionState(q, answers).visible && chosen?.outcome === 'KNOCK_OUT';
    })
    .map((q) => q.code);
}

/* Draft instructions (pol.Job.updateDraft) ------------------------------------------------------------- */

/** A question mapped to a risk field (`mapsToField`) is answered once and feeds that field. */
export function mappedAnswer(
  questions: readonly Question[],
  answers: Record<string, string>,
  field: string,
): string | undefined {
  const q = questions.find((x) => x.mapsToField === field);
  const value = q ? answers[q.code] : undefined;
  return value === undefined || value === '' ? undefined : value;
}

export function vehicleInstruction(
  draft: Draft,
  questions: readonly Question[],
  locator: string | undefined,
  ownerType: string,
): DraftInstruction {
  const v = draft.vehicle;
  const use = mappedAnswer(questions, draft.answers, 'vehicle.usage') ?? 'PRIVATE';
  const fields: Record<string, string | number> = {
    garagingPostcode: v.garagingPostcode.trim(),
    ownerType,
  };
  if (v.powerKw.trim()) fields.powerKw = Number(v.powerKw);
  if (v.fuelType) fields.fuelType = v.fuelType;
  return {
    op: 'SET_VEHICLE',
    vehicle: {
      ...(locator ? { locator } : {}),
      plate: v.plate.trim(),
      ...(v.vin.trim() ? { vin: v.vin.trim() } : {}),
      make: v.make.trim(),
      model: v.model.trim(),
      firstRegistrationYear: Number(v.firstRegistrationYear),
      engineCapacityCc: Number(v.engineCapacityCc),
      use,
      ...(v.value ? { value: { amount: v.value, currency: 'EUR' } } : {}),
      fields,
    },
  };
}

export function driverInstruction(
  draft: Draft,
  questions: readonly Question[],
  vehicleLocator: string,
  driverLocator: string | undefined,
): DraftInstruction | null {
  const party = draft.driver.sameAsPolicyholder ? draft.policyholder : draft.driver.party;
  if (!party) return null;
  const claims = mappedAnswer(questions, draft.answers, 'driver.claimsLast5Years');
  return {
    op: 'SET_DRIVER',
    driver: {
      ...(driverLocator ? { locator: driverLocator } : {}),
      partyId: party.partyId,
      driverType: 'MAIN',
      yearFirstLicensed: Number(draft.driver.yearFirstLicensed),
      vehicleLocator,
      usagePercent: 100,
      claimsLast5Years: claims === undefined ? 0 : Number(claims),
    },
  };
}

export function coverageInstruction(
  coverages: readonly CatalogueCoverage[],
  covers: Record<string, CoverForm>,
  vehicleLocator: string,
): DraftInstruction {
  return {
    op: 'SET_COVERAGES',
    coverages: coverages.map((c) => {
      const form = covers[c.code];
      const selected = c.existence === 'REQUIRED' || form?.selected === true;
      const options: Record<string, string> = {};
      if (selected) {
        for (const term of c.terms) {
          const value =
            form?.terms[term.code] ??
            (term.options?.length === 1 ? term.options[0]?.code : undefined);
          if (value) options[term.code] = value;
        }
      }
      return {
        coverageCode: c.code,
        elementLocator: vehicleLocator,
        selected,
        ...(Object.keys(options).length > 0 ? { options } : {}),
      };
    }),
  };
}

export function answersInstruction(
  questions: readonly Question[],
  answers: Record<string, string>,
): DraftInstruction {
  const visible: Record<string, string> = {};
  for (const q of questions) {
    const value = answers[q.code];
    if (questionState(q, answers).visible && value !== undefined && value.trim() !== '')
      visible[q.code] = value.trim();
  }
  return {
    op: 'SET_ANSWERS',
    questionSet: { questionSetCode: slice.questionSet, questionSetVersion: '1', answers: visible },
  };
}

/** Inputs that change the price: the quote is stale as soon as one differs from what was quoted. */
export function fingerprint(draft: Draft): string {
  const { policyholder, startDate, vehicle, driver, covers, answers } = draft;
  return JSON.stringify({
    p: policyholder?.partyId,
    s: startDate,
    v: vehicle,
    d: {
      same: driver.sameAsPolicyholder,
      party: driver.party?.partyId,
      year: driver.yearFirstLicensed,
    },
    c: covers,
    a: answers,
  });
}

/* Warnings ---------------------------------------------------------------------------------------------- */

export const warningCodes = {
  illustrativeTariff: 'RAT-WARN-ILLUSTRATIVE-TARIFF',
  provisionalTax: 'RAT-WARN-PROVISIONAL-TAX',
} as const;

/**
 * Rating warnings to show with a quote. The quote response has no `warnings` member (contract issue reported), so
 * the provisional-tax warning is derived from the charge lines (`provisional`, or a legal status other than
 * Settled) and the illustrative-tariff warning from the product data: question sets and options flagged
 * `illustrative` mean the rating tables are test data. A `warnings` array on the response, once the contract adds
 * it, wins and is merged in.
 */
export function ratingWarnings(quote: JobQuoteResponse, productIsIllustrative: boolean): string[] {
  const codes = new Set<string>();
  const extra = (quote as { warnings?: { code: string }[] }).warnings;
  for (const w of extra ?? []) codes.add(w.code);
  if (productIsIllustrative) codes.add(warningCodes.illustrativeTariff);
  if (
    quote.charges.some(
      (c) =>
        c.provisional === true ||
        (c.legalStatus !== undefined &&
          c.legalStatus.toUpperCase() !== 'SETTLED' &&
          c.chargeCategory !== 'PREMIUM'),
    )
  ) {
    codes.add(warningCodes.provisionalTax);
  }
  return [...codes];
}
