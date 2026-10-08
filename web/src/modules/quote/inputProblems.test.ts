import { describe, expect, it } from 'vitest';

import { explainInputProblem, focusFieldByLabel } from './inputProblems';
import { coversNeedingVehicleValue, invalidAnswers, vehicleValueIssue } from './state';
import * as fx from '../../test/fixtures';
import type { Question } from '../../api/types';

const problem = (code: string, detail: string, errors?: { field: string; code: string }[]) => ({
  status: 422,
  code,
  detail,
  ...(errors ? { errors } : {}),
});

describe('explainInputProblem', () => {
  it.each([
    [
      'RAT-ERR-INPUT',
      'vehicle.vehicleValue is required to rate OWN-DAMAGE.',
      'vehicleValue',
      'vehicle',
    ],
    [
      'POL-ERR-RATING',
      'Rating failed: RAT-ERR-INPUT x.vehicle.firstRegistrationYear: bad',
      'firstRegistrationYear',
      'vehicle',
    ],
    [
      'POL-ERR-RATING',
      'Rating failed: RAT-ERR-INPUT x.vehicle.engineCapacityCc: bad',
      'engineCapacityCc',
      'vehicle',
    ],
    ['POL-ERR-RATING', 'Rating failed: RAT-ERR-INPUT x.vehicle.usage: bad', 'usage', 'questions'],
    [
      'POL-ERR-RATING',
      'Rating failed: RAT-ERR-INPUT x.driver.dateOfBirth: required',
      'dateOfBirth',
      'driver',
    ],
    [
      'POL-ERR-RATING',
      'Rating failed: RAT-ERR-INPUT x.driver.licenceIssueDate: bad',
      'licenceDate',
      'driver',
    ],
    [
      'POL-ERR-RATING',
      'Rating failed: RAT-ERR-INPUT x.driver.claimsLast5Years: bad',
      'claims',
      'questions',
    ],
    [
      'POL-ERR-RATING',
      'Rating failed: RAT-ERR-INPUT x.riskTree.coverages: none',
      'coverages',
      'covers',
    ],
    [
      'POL-ERR-RATING',
      'Rating failed: RAT-ERR-INPUT effectiveDate: bad',
      'effectiveDate',
      'policyholder',
    ],
    [
      'UW-ERR-SNAPSHOT',
      'The risk snapshot is not a valid motor risk: dateOfBirth',
      'dateOfBirth',
      'driver',
    ],
  ])('%s %s -> %s on %s', (code, detail, field, step) => {
    expect(explainInputProblem(problem(code, detail))).toMatchObject({ field, step });
  });

  it('reads the cover the engine was rating', () => {
    expect(
      explainInputProblem(
        problem('RAT-ERR-INPUT', 'vehicle.vehicleValue is required to rate OWN-DAMAGE.'),
      ),
    ).toMatchObject({ cover: 'OWN-DAMAGE' });
  });

  it('maps POL-ERR-VALIDATION field errors of the draft', () => {
    expect(
      explainInputProblem(
        problem('POL-ERR-VALIDATION', 'x', [
          { field: 'riskTree.drivers', code: 'DRIVER_BIRTH_DATE_MISSING' },
        ]),
      ),
    ).toMatchObject({ field: 'dateOfBirth', step: 'driver' });
  });

  it('leaves other problems alone', () => {
    expect(
      explainInputProblem(problem('POL-ERR-RATING', 'Rounding failed: MKT-ERR-X.')),
    ).toBeNull();
    expect(explainInputProblem(problem('POL-ERR-STALE', 'vehicle.vehicleValue'))).toBeNull();
  });
});

describe('vehicle value rules', () => {
  const covers = fx.catalogue.coverages ?? [];
  it('requires the value only while Own damage is selected', () => {
    expect(coversNeedingVehicleValue(covers, {})).toEqual([]);
    expect(
      coversNeedingVehicleValue(covers, { 'OWN-DAMAGE': { selected: true, terms: {} } }),
    ).toEqual(['OWN-DAMAGE']);
    expect(vehicleValueIssue('', true)).toBe('valueForCover');
    expect(vehicleValueIssue('', false)).toBeUndefined();
    expect(vehicleValueIssue('0', true)).toBe('range');
    expect(vehicleValueIssue('12000.50', true)).toBeUndefined();
  });

  it('flags claims answers the rating would refuse', () => {
    const q = {
      code: 'Q-CLAIMS-5Y',
      answerType: 'INTEGER',
      mapsToField: 'driver.claimsLast5Years',
      required: false,
    } as unknown as Question;
    expect(invalidAnswers([q], { 'Q-CLAIMS-5Y': '2' })).toEqual({});
    expect(invalidAnswers([q], { 'Q-CLAIMS-5Y': 'two' })).toEqual({ 'Q-CLAIMS-5Y': 'integer' });
    expect(invalidAnswers([q], { 'Q-CLAIMS-5Y': '51' })).toEqual({ 'Q-CLAIMS-5Y': 'range' });
  });
});

describe('focusFieldByLabel', () => {
  it('ignores every required-marker asterisk in the label text, not only the first', () => {
    document.body.innerHTML =
      '<label for="plate">Plate * number *</label><input id="plate" />' +
      '<label for="other">Other</label><input id="other" />';
    expect(focusFieldByLabel('Plate number')).toBe(true);
    expect(document.activeElement?.id).toBe('plate');
    expect(focusFieldByLabel('**Plate ** number')).toBe(true);
    document.body.innerHTML = '';
  });
});
