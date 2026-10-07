import { z } from 'zod';

import { validateAfm } from '../../format';

/** The messages the person form shows (already translated by the caller). */
export interface PersonMessages {
  required: string;
  birthFuture: string;
  afm: string;
  postcode: string;
  email: string;
  contact: string;
}

/**
 * Validation of the create-person form. ΑΦΜ uses the same mod-11 rule as the API (`validateAfm`); `today` is the
 * Athens date as `yyyy-mm-dd`, so a birth date cannot be in the future.
 */
export function personSchema(m: PersonMessages, today: string) {
  return z
    .object({
      givenNames: z.string().trim().min(1, m.required).max(200),
      familyName: z.string().trim().min(1, m.required).max(200),
      fatherName: z.string().trim().max(200),
      birthDate: z
        .string()
        .min(1, m.required)
        .refine((v) => v <= today, m.birthFuture),
      afm: z.string().refine((v) => v === '' || validateAfm(v) === null, m.afm),
      street: z.string().trim().min(1, m.required).max(200),
      number: z.string().trim().max(20),
      postcode: z
        .string()
        .trim()
        .regex(/^\d{5}$/, m.postcode),
      locality: z.string().trim().min(1, m.required).max(100),
      email: z
        .string()
        .trim()
        .refine((v) => v === '' || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v), m.email),
      mobile: z.string().trim().max(30),
    })
    .refine((v) => v.email !== '' || v.mobile !== '', { path: ['email'], message: m.contact });
}

/** First error message per field. */
export function fieldErrors(error: z.ZodError): Record<string, string> {
  const out: Record<string, string> = {};
  for (const issue of error.issues) {
    const key = String(issue.path[0] ?? '');
    if (key && !(key in out)) out[key] = issue.message;
  }
  return out;
}
