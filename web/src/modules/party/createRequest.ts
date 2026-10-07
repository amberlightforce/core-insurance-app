import type { PartyCreateRequest } from '../../api/types';

export interface PersonFormValues {
  givenNames: string;
  familyName: string;
  fatherName: string;
  birthDate: string;
  afm: string;
  street: string;
  number: string;
  postcode: string;
  locality: string;
  email: string;
  mobile: string;
}

export const emptyPerson: PersonFormValues = {
  givenNames: '',
  familyName: '',
  fatherName: '',
  birthDate: '',
  afm: '',
  street: '',
  number: '',
  postcode: '',
  locality: '',
  email: '',
  mobile: '',
};

/** The request the form posts: person, optional ΑΦΜ, one legal/mailing address and the contact points. */
export function toCreateRequest(values: PersonFormValues): PartyCreateRequest {
  const optional = (value: string) => (value.trim() === '' ? undefined : value.trim());
  const fatherName = optional(values.fatherName);
  const number = optional(values.number);
  const contactPoints: NonNullable<PartyCreateRequest['contactPoints']> = [];
  if (optional(values.email)) {
    contactPoints.push({
      type: 'EMAIL',
      value: values.email.trim(),
      purpose: 'PERSONAL',
      primary: true,
    });
  }
  if (optional(values.mobile)) {
    contactPoints.push({
      type: 'MOBILE',
      value: values.mobile.trim(),
      purpose: 'PERSONAL',
      primary: true,
    });
  }
  return {
    partyType: 'PERSON',
    person: {
      givenNames: values.givenNames.trim(),
      familyName: values.familyName.trim(),
      ...(fatherName ? { fatherName } : {}),
      birthDate: values.birthDate,
    },
    identifiers: values.afm ? [{ scheme: 'AFM', value: values.afm }] : [],
    addresses: [
      {
        types: ['LEGAL', 'MAILING'],
        primary: true,
        country: 'GR',
        street: values.street.trim(),
        ...(number ? { number } : {}),
        postcode: values.postcode.trim(),
        locality: values.locality.trim(),
      },
    ],
    contactPoints,
    reason: 'NEW_CUSTOMER',
  };
}
