import { describe, expect, it } from 'vitest';

import * as fx from '../../test/fixtures';
import {
  answersInstruction,
  coverageInstruction,
  driverInstruction,
  emptyDraft,
  fingerprint,
  knockOuts,
  missingCoverTerms,
  questionState,
  ratingWarnings,
  unansweredRequired,
  validateDriver,
  validateVehicle,
  vehicleInstruction,
  type Draft,
} from './state';
import { addDays, athensMidnight, athensOffsetMinutes, athensToday, effectiveInstant } from './time';

describe('Athens time helpers', () => {
  it('knows the UTC offset in winter (EET) and summer (EEST)', () => {
    expect(athensOffsetMinutes(new Date('2027-01-15T12:00:00Z'))).toBe(120);
    expect(athensOffsetMinutes(new Date('2027-07-15T12:00:00Z'))).toBe(180);
  });

  it('computes 00:00 Athens as a UTC instant, also around the DST change', () => {
    expect(athensMidnight('2027-01-01').toISOString()).toBe('2026-12-31T22:00:00.000Z');
    expect(athensMidnight('2027-07-01').toISOString()).toBe('2027-06-30T21:00:00.000Z');
    // Clocks go forward at 03:00 on 2027-03-28: midnight that day is still EET, the next one EEST.
    expect(athensMidnight('2027-03-28').toISOString()).toBe('2027-03-27T22:00:00.000Z');
    expect(athensMidnight('2027-03-29').toISOString()).toBe('2027-03-28T21:00:00.000Z');
  });

  it('reads today in Athens, not UTC', () => {
    expect(athensToday(new Date('2026-12-31T22:30:00Z'))).toBe('2027-01-01');
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
  });

  it('starts new business no earlier than one hour from now, and later dates at midnight', () => {
    const now = new Date('2026-10-07T10:00:00Z');
    expect(effectiveInstant('2026-10-07', now)).toBe('2026-10-07T11:00:00Z');
    expect(effectiveInstant('2026-10-08', now)).toBe('2026-10-07T21:00:00Z');
  });
});

describe('quote validation', () => {
  it('flags missing and invalid vehicle fields', () => {
    const issues = validateVehicle(
      { plate: 'ABC', vin: '', make: '', model: 'Yaris', firstRegistrationYear: '1850', engineCapacityCc: '0', powerKw: '', fuelType: '', value: '', garagingPostcode: '123' },
      2026,
    );
    expect(issues).toMatchObject({ plate: 'plate', make: 'required', firstRegistrationYear: 'year', engineCapacityCc: 'range', garagingPostcode: 'postcode' });
    expect(issues.model).toBeUndefined();
  });

  it('accepts a complete vehicle and driver', () => {
    const vehicle = { plate: 'ΙΚΧ1234', vin: '', make: 'Toyota', model: 'Yaris', firstRegistrationYear: '2021', engineCapacityCc: '1400', powerKw: '72', fuelType: 'PETROL', value: '15000.00', garagingPostcode: '11526' };
    expect(validateVehicle(vehicle, 2026)).toEqual({});
    expect(validateDriver({ sameAsPolicyholder: true, party: null, yearFirstLicensed: '2010' }, 2026)).toEqual({});
    expect(validateDriver({ sameAsPolicyholder: false, party: null, yearFirstLicensed: '2030' }, 2026)).toEqual({ party: 'required', yearFirstLicensed: 'year' });
  });

  it('requires the terms of required and selected covers only', () => {
    const coverages = fx.catalogue.coverages ?? [];
    // MTPL has a two-option list the user must choose; own damage is not selected, so not required.
    expect(missingCoverTerms(coverages, {})).toEqual(['MTPL.BI_PER_PERSON']);
    expect(missingCoverTerms(coverages, { MTPL: { selected: true, terms: { BI_PER_PERSON: 'BI-1300K' } } })).toEqual([]);
    expect(
      missingCoverTerms(coverages, {
        MTPL: { selected: true, terms: { BI_PER_PERSON: 'BI-1300K' } },
        'OWN-DAMAGE': { selected: true, terms: { DEDUCTIBLE: 'DED-0' } },
      }),
    ).toEqual(['OWN-DAMAGE.SUM_INSURED']);
  });
});

describe('question set', () => {
  const questions = fx.questionSet.questionSet.questions;

  it('applies visibleWhen/requiredWhen and finds unanswered required questions', () => {
    expect(unansweredRequired(questions, {})).toEqual(['Q-USAGE', 'Q-HIRE-REWARD']);
    expect(unansweredRequired(questions, { 'Q-USAGE': 'PRIVATE', 'Q-HIRE-REWARD': 'NO' })).toEqual([]);
    const conditional = {
      code: 'Q-BUSINESS-USE',
      required: false,
      visibleWhen: { question: 'Q-USAGE', answeredWith: ['BUSINESS'] },
      requiredWhen: { question: 'Q-USAGE', answeredWith: ['BUSINESS'] },
    } as unknown as (typeof questions)[number];
    expect(questionState(conditional, {})).toEqual({ visible: false, required: false });
    expect(questionState(conditional, { 'Q-USAGE': 'BUSINESS' })).toEqual({ visible: true, required: true });
  });

  it('detects knock-out answers', () => {
    expect(knockOuts(questions, { 'Q-HIRE-REWARD': 'YES' })).toEqual(['Q-HIRE-REWARD']);
    expect(knockOuts(questions, { 'Q-HIRE-REWARD': 'NO' })).toEqual([]);
  });
});

describe('draft instructions (pol.Job.updateDraft)', () => {
  const draft: Draft = {
    ...emptyDraft('2026-10-08'),
    policyholder: { partyId: fx.partyId, label: 'Δοκιμή Διεπαφής' },
    vehicle: { plate: 'ΙΚΧ1234', vin: '', make: 'Toyota', model: 'Yaris', firstRegistrationYear: '2021', engineCapacityCc: '1400', powerKw: '72', fuelType: 'PETROL', value: '15000.00', garagingPostcode: '11526' },
    driver: { sameAsPolicyholder: true, party: null, yearFirstLicensed: '2010' },
    covers: { MTPL: { selected: true, terms: { BI_PER_PERSON: 'BI-1300K' } }, 'OWN-DAMAGE': { selected: true, terms: { SUM_INSURED: '15000.00', DEDUCTIBLE: 'DED-150' } } },
    answers: { 'Q-USAGE': 'PRIVATE', 'Q-HIRE-REWARD': 'NO', 'Q-CLAIMS-5Y': '2' },
  };
  const questions = fx.questionSet.questionSet.questions;

  it('builds the vehicle with usage from the mapped question and the open fields', () => {
    expect(vehicleInstruction(draft, questions, undefined, 'PERSON')).toEqual({
      op: 'SET_VEHICLE',
      vehicle: {
        plate: 'ΙΚΧ1234',
        make: 'Toyota',
        model: 'Yaris',
        firstRegistrationYear: 2021,
        engineCapacityCc: 1400,
        use: 'PRIVATE',
        value: { amount: '15000.00', currency: 'EUR' },
        fields: { garagingPostcode: '11526', ownerType: 'PERSON', powerKw: 72, fuelType: 'PETROL' },
      },
    });
    expect(vehicleInstruction(draft, questions, 'loc-1', 'PERSON').vehicle?.locator).toBe('loc-1');
  });

  it('builds the main driver (policyholder or another party) with claims from the mapped question', () => {
    expect(driverInstruction(draft, questions, 'v-1', undefined)).toEqual({
      op: 'SET_DRIVER',
      driver: { partyId: fx.partyId, driverType: 'MAIN', yearFirstLicensed: 2010, vehicleLocator: 'v-1', usagePercent: 100, claimsLast5Years: 2 },
    });
    const other = { ...draft, driver: { ...draft.driver, sameAsPolicyholder: false, party: { partyId: 'p-2', label: 'X' } } };
    expect(driverInstruction(other, questions, 'v-1', 'd-1')?.driver?.partyId).toBe('p-2');
    expect(driverInstruction({ ...draft, policyholder: null }, questions, 'v-1', undefined)).toBeNull();
  });

  it('sends every cover with its selection and chosen terms; compulsory covers are always selected', () => {
    const instruction = coverageInstruction(fx.catalogue.coverages ?? [], { 'OWN-DAMAGE': draft.covers['OWN-DAMAGE'] ?? { selected: false, terms: {} } }, 'v-1');
    expect(instruction.coverages).toEqual([
      { coverageCode: 'MTPL', elementLocator: 'v-1', selected: true },
      { coverageCode: 'OWN-DAMAGE', elementLocator: 'v-1', selected: true, options: { SUM_INSURED: '15000.00', DEDUCTIBLE: 'DED-150' } },
    ]);
  });

  it('sends only visible, non-empty answers', () => {
    const instruction = answersInstruction(questions, { ...draft.answers, 'Q-OLD': 'x', 'Q-CLAIMS-5Y': ' ' });
    expect(instruction.questionSet?.answers).toEqual({ 'Q-USAGE': 'PRIVATE', 'Q-HIRE-REWARD': 'NO' });
  });

  it('changes the fingerprint when a price input changes', () => {
    const before = fingerprint(draft);
    expect(fingerprint({ ...draft })).toBe(before);
    expect(fingerprint({ ...draft, answers: { ...draft.answers, 'Q-CLAIMS-5Y': '3' } })).not.toBe(before);
  });
});

describe('rating warnings', () => {
  it('derives provisional-tax from the charge lines and illustrative-tariff from the product data', () => {
    expect(ratingWarnings(fx.quote(), true).sort()).toEqual(['RAT-WARN-ILLUSTRATIVE-TARIFF', 'RAT-WARN-PROVISIONAL-TAX']);
    expect(ratingWarnings(fx.quote(), false)).toEqual(['RAT-WARN-PROVISIONAL-TAX']);
    const settled = fx.quote({ charges: fx.quote().charges.map((c) => ({ ...c, provisional: false, legalStatus: 'Settled' })) });
    expect(ratingWarnings(settled, false)).toEqual([]);
  });

  it('merges warnings the API sends itself', () => {
    const withWarnings = { ...fx.quote({ charges: [] }), warnings: [{ code: 'RAT-WARN-ILLUSTRATIVE-TARIFF' }] };
    expect(ratingWarnings(withWarnings, false)).toEqual(['RAT-WARN-ILLUSTRATIVE-TARIFF']);
  });
});
