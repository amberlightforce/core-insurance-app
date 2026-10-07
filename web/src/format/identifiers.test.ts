import { describe, expect, it } from 'vitest';

import {
  formatAfm,
  formatIban,
  formatPlate,
  ibanMod97,
  identifierCodecs,
  isValidAfmChecksum,
  normalizeAfm,
  normalizeIban,
  normalizePlate,
  normalizeReference,
  normalizeVin,
  validateAfm,
  validateIban,
  validatePlate,
  validateReference,
  validateVin,
  vinCheckDigit,
} from './identifiers';

describe('ΑΦΜ', () => {
  it('keeps digits only, at most 9', () => {
    expect(normalizeAfm('090 000 045')).toBe('090000045');
    expect(normalizeAfm('ΑΦΜ: 09-00-00-04-5 99')).toBe('090000045');
  });

  it('groups by three', () => {
    expect(formatAfm('090000045')).toBe('090 000 045');
    expect(formatAfm('0900')).toBe('090 0');
    expect(formatAfm('')).toBe('');
  });

  it('accepts a valid mod-11 check digit', () => {
    expect(isValidAfmChecksum('090000045')).toBe(true);
    expect(validateAfm('090000045')).toBeNull();
    // 0*256+9*128+4*64+... another known-valid number
    expect(isValidAfmChecksum('094014201')).toBe(true);
  });

  it('treats a check remainder of 10 as 0', () => {
    // Weighted sum of 00000005 = 5*2 = 10 → 10 mod 11 = 10 → 10 mod 10 = 0.
    expect(isValidAfmChecksum('000000050')).toBe(true);
  });

  it('rejects a wrong check digit and all zeros', () => {
    expect(validateAfm('090000046')).toEqual({ code: 'checksum', severity: 'error' });
    expect(isValidAfmChecksum('000000000')).toBe(false);
  });

  it('rejects the wrong length', () => {
    expect(validateAfm('09000004')).toEqual({
      code: 'length',
      severity: 'error',
      params: { length: 9 },
    });
  });
});

describe('IBAN', () => {
  const greek = 'GR1601101250000000012300695';

  it('normalises to upper-case letters and digits', () => {
    expect(normalizeIban('gr16 0110 1250 0000 0001 2300 695')).toBe(greek);
    expect(normalizeIban('GR16-0110-1250')).toBe('GR1601101250');
  });

  it('groups by four', () => {
    expect(formatIban(greek)).toBe('GR16 0110 1250 0000 0001 2300 695');
  });

  it('validates the Greek example (27 characters, mod 97 = 1)', () => {
    expect(greek).toHaveLength(27);
    expect(ibanMod97(greek)).toBe(1);
    expect(validateIban(greek)).toBeNull();
  });

  it('validates other SEPA examples', () => {
    expect(validateIban('DE89370400440532013000')).toBeNull();
    expect(validateIban('GB82WEST12345698765432')).toBeNull();
    expect(validateIban('CY17002001280000001200527600')).toBeNull();
  });

  it('rejects a bad check', () => {
    expect(validateIban('GR1701101250000000012300695')).toEqual({
      code: 'checksum',
      severity: 'error',
    });
  });

  it('rejects the wrong length for the country', () => {
    expect(validateIban('GR160110125000000001230069')).toEqual({
      code: 'length',
      severity: 'error',
      params: { country: 'GR', length: 27 },
    });
  });

  it('rejects a missing country code', () => {
    expect(validateIban('1601101250000000012300695')?.code).toBe('country');
  });

  it('uses a length range for countries outside the table', () => {
    expect(validateIban('ZZ12345')?.code).toBe('lengthRange');
  });
});

describe('Greek plate', () => {
  it('converts Latin lookalikes to Greek capitals', () => {
    expect(normalizePlate('ikx1234')).toEqual({ value: 'ΙΚΧ1234', converted: true });
    expect(normalizePlate('ABE-1234')).toEqual({ value: 'ΑΒΕ1234', converted: true });
    // P is the Latin lookalike of Greek Ρ (rho), Y of Υ, H of Η.
    expect(normalizePlate('PYH 0001').value).toBe('ΡΥΗ0001');
  });

  it('upper-cases Greek input without conversion', () => {
    expect(normalizePlate('ικχ-1234')).toEqual({ value: 'ΙΚΧ1234', converted: false });
    expect(normalizePlate('ΊΚΧ1234').value).toBe('ΙΚΧ1234');
  });

  it('keeps other letters so that validation can explain them', () => {
    expect(normalizePlate('ΓΔΛ1234').value).toBe('ΓΔΛ1234');
    expect(normalizePlate('CDF1234').value).toBe('CDF1234');
  });

  it('formats with a hyphen once the digits start', () => {
    expect(formatPlate('ΙΚΧ1234')).toBe('ΙΚΧ-1234');
    expect(formatPlate('ΙΚ')).toBe('ΙΚ');
    expect(formatPlate('ΙΚΧ1')).toBe('ΙΚΧ-1');
  });

  it('validates the 14 letters and the ΑΑΑ-0000 shape', () => {
    expect(validatePlate('ΙΚΧ1234')).toBeNull();
    expect(validatePlate('ΓΚΧ1234')?.code).toBe('letters');
    expect(validatePlate('CKX1234')?.code).toBe('letters');
    expect(validatePlate('ΙΚ1234')?.code).toBe('format');
    expect(validatePlate('ΙΚΧ123')?.code).toBe('format');
  });
});

describe('VIN', () => {
  it('normalises to 17 upper-case characters', () => {
    expect(normalizeVin('1m8gdm9axkp042788')).toBe('1M8GDM9AXKP042788');
    expect(normalizeVin('WVW ZZZ 1JZ XW 000001 99')).toBe('WVWZZZ1JZXW000001');
  });

  it('computes the check digit', () => {
    expect(vinCheckDigit('1M8GDM9AXKP042788')).toBe('X');
    expect(vinCheckDigit('11111111111111111')).toBe('1');
  });

  it('accepts a valid VIN', () => {
    expect(validateVin('1M8GDM9AXKP042788')).toBeNull();
  });

  it('rejects I, O and Q', () => {
    expect(validateVin('1M8GDM9AXKP04278O')?.code).toBe('chars');
    expect(validateVin('IM8GDM9AXKP042788')).toEqual({ code: 'chars', severity: 'error' });
  });

  it('rejects the wrong length', () => {
    expect(validateVin('1M8GDM9AXKP04278')?.code).toBe('length');
  });

  it('treats a wrong check digit as a warning only', () => {
    expect(validateVin('WVWZZZ1JZXW000001')).toEqual({ code: 'checkDigit', severity: 'warning' });
  });
});

describe('references', () => {
  it('upper-cases Greek without tonos and drops spaces', () => {
    expect(normalizeReference('ασφ-2026 / 0412')).toBe('ΑΣΦ-2026/0412');
  });

  it('validates against a scheme pattern when given', () => {
    expect(validateReference('ΑΣΦ-2026-0412', /^ΑΣΦ-\d{4}-\d{4}$/u)).toBeNull();
    expect(validateReference('ΑΣΦ-26', /^ΑΣΦ-\d{4}-\d{4}$/u)?.code).toBe('format');
    expect(validateReference('anything')).toBeNull();
  });
});

describe('codecs', () => {
  it('keep normalize prefix-stable so the caret can follow significant characters', () => {
    const samples: [keyof typeof identifierCodecs, string][] = [
      ['afm', '090 000 045'],
      ['iban', 'gr16 0110 1250 0000 0001 2300 695'],
      ['plate', 'ikx-1234'],
      ['vin', '1m8gdm9axkp042788'],
    ];
    for (const [kind, input] of samples) {
      const codec = identifierCodecs[kind];
      const full = codec.normalize(input);
      for (let i = 0; i <= input.length; i++) {
        expect(full.startsWith(codec.normalize(input.slice(0, i)))).toBe(true);
      }
    }
  });
});
