import { toMinor } from '../../../format';
import { emptyVehicle, type VehicleForm } from '../../quote/state';
import { addDays, athensMidnight, athensToday } from '../../quote/time';
import type { DraftInstruction, ServicingPreview, Vehicle } from './api';

export type Due =
  | { kind: 'refund'; amount: ServicingPreview['refundDue'] }
  | { kind: 'additional'; amount: ServicingPreview['additionalDue'] }
  | { kind: 'neutral' };

/** Refund due vs additional due. Both are always set by the server; at most one is non-zero. */
export function dueOf(preview: ServicingPreview): Due {
  if (toMinor(preview.refundDue.amount) > 0n) return { kind: 'refund', amount: preview.refundDue };
  if (toMinor(preview.additionalDue.amount) > 0n)
    return { kind: 'additional', amount: preview.additionalDue };
  return { kind: 'neutral' };
}

/**
 * The effectiveAt instant of a servicing change for a calendar date: today means now (the earliest a CSR may
 * choose, D-SL3-08), any later date 00:00 Athens. Whole seconds, so a retry sends an identical payload.
 */
export function changeInstant(date: string, now: Date = new Date()): string {
  const instant = date <= athensToday(now) ? now : athensMidnight(date);
  return instant.toISOString().replace(/\.\d{3}Z$/, 'Z');
}

/** The Athens calendar date of an instant string (term bounds arrive as instants with an offset). */
export function athensDateOf(instant: string): string {
  return athensToday(new Date(instant));
}

/** Last day the term covers (its end is half-open). */
export function lastDayOfTerm(termEnd: string): string {
  return addDays(athensDateOf(termEnd), -1);
}

/* Vehicle edit and replace --------------------------------------------------------------------------- */

/** The vehicle form of the quote wizard, filled from the vehicle that is on the policy now. */
export function formOf(vehicle: Vehicle | undefined): VehicleForm {
  if (!vehicle) return { ...emptyVehicle };
  const fields = (vehicle.fields ?? {}) as Record<string, unknown>;
  const text = (v: unknown) => (typeof v === 'string' || typeof v === 'number' ? String(v) : '');
  return {
    plate: vehicle.plate,
    vin: vehicle.vin ?? '',
    make: vehicle.make ?? '',
    model: vehicle.model ?? '',
    firstRegistrationYear: vehicle.firstRegistrationYear ? String(vehicle.firstRegistrationYear) : '',
    engineCapacityCc: vehicle.engineCapacityCc ? String(vehicle.engineCapacityCc) : '',
    powerKw: text(fields.powerKw),
    fuelType: text(fields.fuelType),
    value: vehicle.value?.amount ?? '',
    garagingPostcode: text(fields.garagingPostcode),
  };
}

/** The fields shown in the before/after diff, in display order. */
export const diffFields = [
  'plate',
  'vin',
  'make',
  'model',
  'firstRegistrationYear',
  'engineCapacityCc',
  'powerKw',
  'fuelType',
  'value',
  'garagingPostcode',
] as const satisfies readonly (keyof VehicleForm)[];

export interface DiffRow {
  field: (typeof diffFields)[number];
  before: string;
  after: string;
}

/** Rows whose value differs between the policy's vehicle and the edited one (element, field, before, after). */
export function vehicleDiff(before: VehicleForm, after: VehicleForm): DiffRow[] {
  return diffFields
    .filter((f) => before[f].trim() !== after[f].trim())
    .map((field) => ({ field, before: before[field].trim(), after: after[field].trim() }));
}

/** The SET_VEHICLE instruction for an edit (locator kept, other element fields preserved) or a replacement (no locator). */
export function vehicleChangeInstruction(
  form: VehicleForm,
  existing: Vehicle | undefined,
  keepLocator: boolean,
): DraftInstruction {
  const base = keepLocator ? (existing?.fields ?? {}) : {};
  const fields: Record<string, unknown> = { ...(base as Record<string, unknown>) };
  fields.garagingPostcode = form.garagingPostcode.trim();
  if (form.powerKw.trim()) fields.powerKw = Number(form.powerKw);
  else delete fields.powerKw;
  if (form.fuelType) fields.fuelType = form.fuelType;
  else delete fields.fuelType;
  if (!fields.ownerType) fields.ownerType = 'PERSON';
  return {
    op: 'SET_VEHICLE',
    vehicle: {
      ...(keepLocator && existing?.locator ? { locator: existing.locator } : {}),
      plate: form.plate.trim(),
      ...(form.vin.trim() ? { vin: form.vin.trim() } : {}),
      make: form.make.trim(),
      model: form.model.trim(),
      firstRegistrationYear: Number(form.firstRegistrationYear),
      engineCapacityCc: Number(form.engineCapacityCc),
      use: existing?.use ?? 'PRIVATE',
      ...(form.value ? { value: { amount: form.value, currency: 'EUR' } } : {}),
      fields: fields as NonNullable<Vehicle['fields']>,
    },
  };
}

/** Whole days between two Athens calendar dates (`to - from`). */
export function daysBetween(from: string, to: string): number {
  const ms = (d: string) => {
    const [y = 0, m = 1, day = 1] = d.split('-').map(Number);
    return Date.UTC(y, m - 1, day);
  };
  return Math.round((ms(to) - ms(from)) / 86_400_000);
}

/** Renewal lead time in days (PRD-05 §10.2 `pol.renewal.lead_days`, D-SL3-08: illustrative; the server decides). */
export const renewalLeadDays = 45;
