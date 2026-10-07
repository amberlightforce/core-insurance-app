import { useCallback } from 'react';
import { useTranslation } from 'react-i18next';

import type { IdentifierIssue, IdentifierKind } from '../../../format/identifiers';

/**
 * Message for a validation issue, following the formula «what + where + how to fix» (Part 4 §8.4). Keys are
 * spelled out so that the i18n lint can check them.
 */
export function useIssueMessage(): (kind: IdentifierKind, issue: IdentifierIssue) => string {
  const { t } = useTranslation('ds');
  return useCallback(
    (kind, issue) => {
      const p = issue.params ?? {};
      switch (`${kind}.${issue.code}`) {
        case 'afm.length':
          return t('identifierField.errors.afmLength', p);
        case 'afm.checksum':
          return t('identifierField.errors.afmChecksum', p);
        case 'iban.country':
          return t('identifierField.errors.ibanCountry', p);
        case 'iban.length':
          return t('identifierField.errors.ibanLength', p);
        case 'iban.lengthRange':
          return t('identifierField.errors.ibanLengthRange', p);
        case 'iban.checksum':
          return t('identifierField.errors.ibanChecksum', p);
        case 'plate.letters':
          return t('identifierField.errors.plateLetters', p);
        case 'plate.format':
          return t('identifierField.errors.plateFormat', p);
        case 'vin.chars':
          return t('identifierField.errors.vinChars', p);
        case 'vin.length':
          return t('identifierField.errors.vinLength', p);
        case 'vin.checkDigit':
          return t('identifierField.errors.vinCheckDigit', p);
        default:
          return t('identifierField.errors.referenceFormat', p);
      }
    },
    [t],
  );
}
